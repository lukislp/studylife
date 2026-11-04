// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using StudyLife.Server.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Tests for Database:TlsCaCertificatePath (PostgresTlsConfiguration). The handshake tests run a
/// minimal fake PostgreSQL server on loopback that speaks the real wire-protocol start
/// (SSLRequest -> 'S' -> TLS handshake -> StartupMessage), so it is Npgsql itself that decides
/// whether the server certificate is accepted - not a hand-written imitation of its rules.
/// </summary>
public sealed class PostgresTlsConfigurationTests : IDisposable
{
    // The prod host: the app talks to the CNPG pooler service (k8s/11-pooler.yaml).
    private const string PoolerHost = "studylife-pg-pooler";

    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _otherCa;
    private readonly X509Certificate2 _loopbackLeaf;
    private readonly X509Certificate2 _loopbackLeafFromOtherCa;
    private readonly X509Certificate2 _poolerOnlyLeaf;
    private readonly string _tempDir;

    public PostgresTlsConfigurationTests()
    {
        _ca = CreateCa("studylife-pg-test-ca");
        _otherCa = CreateCa("some-other-ca");
        _loopbackLeaf = CreateLeaf(_ca, "localhost", IPAddress.Loopback);
        _loopbackLeafFromOtherCa = CreateLeaf(_otherCa, "localhost", IPAddress.Loopback);
        _poolerOnlyLeaf = CreateLeaf(_ca, PoolerHost, ip: null);
        _tempDir = Directory.CreateTempSubdirectory("studylife-pg-tls-tests-").FullName;
    }

    public void Dispose()
    {
        foreach (var cert in new[] { _ca, _otherCa, _loopbackLeaf, _loopbackLeafFromOtherCa, _poolerOnlyLeaf })
            cert.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    // ---- Apply: decision table ----------------------------------------------------------------

    [Fact]
    public void Apply_NoCaPath_ReturnsConnectionStringUnchanged()
    {
        const string cs = "Host=studylife-pg-pooler;Port=5432;Database=studylife;Username=u;Password=p;Max Auto Prepare=0";

        Assert.Same(cs, PostgresTlsConfiguration.Apply(cs, new DatabaseOptions()));
        Assert.Same(cs, PostgresTlsConfiguration.Apply(cs, new DatabaseOptions { TlsCaCertificatePath = "  " }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(";SSL Mode=Disable")]
    [InlineData(";SSL Mode=Prefer")]
    [InlineData(";SSL Mode=Require")]
    [InlineData(";SSL Mode=VerifyCA")]
    public void Apply_CaPath_ForcesVerifyFullWithThatRootAndKeepsTheRest(string sslPart)
    {
        var caPath = WritePem("ca.crt", _ca);
        var cs = "Host=studylife-pg-pooler;Port=5432;Database=studylife;Username=u;Password=p;Max Auto Prepare=0" + sslPart;

        var result = new NpgsqlConnectionStringBuilder(
            PostgresTlsConfiguration.Apply(cs, new DatabaseOptions { TlsCaCertificatePath = $" {caPath} " }));

        Assert.Equal(SslMode.VerifyFull, result.SslMode);
        Assert.Equal(caPath, result.RootCertificate);
        Assert.Equal(PoolerHost, result.Host);
        Assert.Equal("studylife", result.Database);
        Assert.Equal("p", result.Password);
        Assert.Equal(0, result.MaxAutoPrepare);
        Assert.False(result.CheckCertificateRevocation);
    }

    [Fact]
    public void Apply_SameRootCertificateAlreadyInConnectionString_IsAccepted()
    {
        var caPath = WritePem("ca.crt", _ca);

        var result = new NpgsqlConnectionStringBuilder(PostgresTlsConfiguration.Apply(
            $"Host=h;Username=u;Root Certificate={caPath}", new DatabaseOptions { TlsCaCertificatePath = caPath }));

        Assert.Equal(SslMode.VerifyFull, result.SslMode);
        Assert.Equal(caPath, result.RootCertificate);
    }

    [Fact]
    public void Apply_DifferentRootCertificateInConnectionString_FailsAtStartup()
    {
        var caPath = WritePem("ca.crt", _ca);
        var otherPath = WritePem("other.crt", _otherCa);

        Assert.Throws<InvalidOperationException>(() => PostgresTlsConfiguration.Apply(
            $"Host=h;Username=u;Root Certificate={otherPath}", new DatabaseOptions { TlsCaCertificatePath = caPath }));
    }

    [Fact]
    public void Apply_TrustServerCertificateInConnectionString_FailsAtStartup()
    {
        var caPath = WritePem("ca.crt", _ca);

        Assert.Throws<InvalidOperationException>(() => PostgresTlsConfiguration.Apply(
            "Host=h;Username=u;Trust Server Certificate=true", new DatabaseOptions { TlsCaCertificatePath = caPath }));
    }

    [Fact]
    public void Apply_MissingCaFile_FailsAtStartup()
    {
        Assert.Throws<FileNotFoundException>(() => PostgresTlsConfiguration.Apply(
            "Host=h;Username=u", new DatabaseOptions { TlsCaCertificatePath = Path.Combine(_tempDir, "missing.crt") }));
    }

    [Fact]
    public void Apply_CaFileWithoutCertificate_FailsAtStartup()
    {
        var path = Path.Combine(_tempDir, "empty.crt");
        File.WriteAllText(path, "not a certificate");

        Assert.Throws<InvalidOperationException>(() => PostgresTlsConfiguration.Apply(
            "Host=h;Username=u", new DatabaseOptions { TlsCaCertificatePath = path }));
    }

    // ---- Real handshakes: Npgsql against a fake PostgreSQL server -----------------------------

    [Fact]
    public async Task Handshake_TrustedCaAndMatchingHost_Succeeds()
    {
        var caPath = WritePem("ca.crt", _ca);

        var outcome = await ConnectWithCaAsync(caPath, _loopbackLeaf);

        Assert.True(outcome.ServerReceivedStartupOverTls, outcome.ClientError?.ToString());
    }

    [Fact]
    public async Task Handshake_CertificateFromOtherCa_Fails()
    {
        var caPath = WritePem("ca.crt", _ca);

        var outcome = await ConnectWithCaAsync(caPath, _loopbackLeafFromOtherCa);

        Assert.False(outcome.ServerReceivedStartupOverTls);
        AssertRejectedCertificate(outcome.ClientError);
    }

    [Fact]
    public async Task Handshake_TrustedCaButCertificateForOtherHost_Fails()
    {
        // Exactly the prod situation before the pooler names were added to the CNPG server
        // certificate: chain fine, but the name the client connected to is not in the SANs.
        var caPath = WritePem("ca.crt", _ca);

        var outcome = await ConnectWithCaAsync(caPath, _poolerOnlyLeaf);

        Assert.False(outcome.ServerReceivedStartupOverTls);
        AssertRejectedCertificate(outcome.ClientError);
    }

    [Fact]
    public async Task Handshake_ServerRefusesTls_DoesNotFallBackToPlaintext()
    {
        var caPath = WritePem("ca.crt", _ca);

        var outcome = await ConnectWithCaAsync(caPath, serverCertificate: null);

        Assert.False(outcome.ServerReceivedPlaintextStartup);
        Assert.NotNull(outcome.ClientError);
    }

    [Fact]
    public async Task Handshake_CaFileReplacedAtRuntime_IsPickedUpWithoutRestart()
    {
        // CNPG rotates its CA, kubelet updates the projected file. The connection string built
        // once at startup must follow the file, not a copy taken at startup.
        var caPath = WritePem("ca.crt", _ca);
        var connectionString = BuildConnectionString(caPath, port: 1);

        File.WriteAllText(caPath, _otherCa.ExportCertificatePem());
        var outcome = await ConnectAsync(connectionString, _loopbackLeafFromOtherCa);

        Assert.True(outcome.ServerReceivedStartupOverTls, outcome.ClientError?.ToString());
    }

    // ---- helpers ------------------------------------------------------------------------------

    private sealed record Outcome(bool ServerReceivedStartupOverTls, bool ServerReceivedPlaintextStartup, Exception? ClientError);

    /// <summary>The client must have failed in the TLS authentication itself, not somewhere
    /// later (which would mean the certificate was accepted).</summary>
    private static void AssertRejectedCertificate(Exception? error)
    {
        Assert.NotNull(error);
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return;
        }
        Assert.Fail($"Expected an AuthenticationException in the chain, got: {error}");
    }
    private static string BuildConnectionString(string caPath, int port) =>
        PostgresTlsConfiguration.Apply(
            $"Host=127.0.0.1;Port={port};Database=studylife;Username=u;Password=p;Pooling=false;Timeout=10;Gss Encryption Mode=Disable",
            new DatabaseOptions { TlsCaCertificatePath = caPath });

    private static Task<Outcome> ConnectWithCaAsync(string caPath, X509Certificate2? serverCertificate) =>
        ConnectAsync(BuildConnectionString(caPath, port: 1), serverCertificate);

    /// <summary>Opens an NpgsqlConnection with <paramref name="connectionString"/> (its port is
    /// replaced by the fake server's) and reports what the server saw.</summary>
    private static async Task<Outcome> ConnectAsync(string connectionString, X509Certificate2? serverCertificate)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var serverTask = Task.Run(() => FakeServerAsync(listener, serverCertificate, cts.Token), cts.Token);

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Port = port };
        Exception? clientError = null;
        await using (var connection = new NpgsqlConnection(builder.ConnectionString))
        {
            try
            {
                await connection.OpenAsync(cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Expected in every test: the fake server never completes authentication.
                clientError = ex;
            }
        }
        var (overTls, plaintext) = await serverTask;
        return new Outcome(overTls, plaintext, clientError);
    }

    /// <summary>
    /// Answers the pre-startup requests like PostgreSQL does: GSSENCRequest is declined,
    /// SSLRequest is accepted ('S' + TLS handshake) when a certificate is given and declined
    /// ('N') otherwise. Returns whether a StartupMessage then arrived over TLS or in plaintext,
    /// and closes the connection either way.
    /// </summary>
    private static async Task<(bool OverTls, bool Plaintext)> FakeServerAsync(
        TcpListener listener, X509Certificate2? serverCertificate, CancellationToken ct)
    {
        const int sslRequestCode = 80877103;
        const int gssEncRequestCode = 80877104;
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            var stream = socket.GetStream();
            var header = new byte[8];
            while (true)
            {
                await stream.ReadExactlyAsync(header, ct);
                var code = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
                if (code == gssEncRequestCode || (code == sslRequestCode && serverCertificate is null))
                {
                    await stream.WriteAsync("N"u8.ToArray(), ct);
                    continue;
                }
                if (code != sslRequestCode)
                    return (false, true); // a StartupMessage without TLS

                await stream.WriteAsync("S"u8.ToArray(), ct);
                await using var ssl = new SslStream(stream);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                }, ct);
                var startupLength = new byte[4];
                await ssl.ReadExactlyAsync(startupLength, ct);
                return (BinaryPrimitives.ReadInt32BigEndian(startupLength) > 8, false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client aborting (handshake rejected, or closing after a declined SSLRequest)
            // is the expected outcome of the failure tests.
            return (false, false);
        }
    }

    private string WritePem(string name, params X509Certificate2[] certs)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, string.Join('\n', certs.Select(c => c.ExportCertificatePem())));
        return path;
    }

    private static X509Certificate2 CreateCa(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca, string dnsName, IPAddress? ip)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        if (ip is not null)
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Round trip through PKCS#12 so the key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }
}
