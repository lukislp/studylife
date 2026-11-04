// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StudyLife.Server.Configuration;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// Regression tests for the TLS certificate validation that replaced "accept any certificate"
/// on the Redis client and "chain only, ignore the host name" on the Kubernetes API client.
/// Certificates are generated per test class run (a private CA plus leaves), the handshake tests
/// run a real SslStream server on loopback so the SslPolicyErrors values are the ones the
/// runtime actually produces, not hand-picked flags.
/// </summary>
public sealed class PrivateCaCertificateValidatorTests : IDisposable
{
    private const string ServerName = "redis-cluster.studylife-scale.svc.cluster.local";

    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _otherCa;
    private readonly X509Certificate2 _leaf;
    private readonly X509Certificate2 _leafWithIp;
    private readonly X509Certificate2 _expiredLeaf;
    private readonly X509Certificate2 _leafFromOtherCa;
    private readonly string _tempDir;

    public PrivateCaCertificateValidatorTests()
    {
        _ca = CreateCa("studylife-test-ca");
        _otherCa = CreateCa("some-other-ca");
        _leaf = CreateLeaf(_ca, ServerName, ip: null, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _leafWithIp = CreateLeaf(_ca, "localhost", IPAddress.Loopback, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _expiredLeaf = CreateLeaf(_ca, ServerName, ip: null, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        _leafFromOtherCa = CreateLeaf(_otherCa, ServerName, ip: null, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _tempDir = Directory.CreateTempSubdirectory("studylife-tls-tests-").FullName;
    }

    public void Dispose()
    {
        foreach (var cert in new[] { _ca, _otherCa, _leaf, _leafWithIp, _expiredLeaf, _leafFromOtherCa })
            cert.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    // ---- Validate: decision table -------------------------------------------------------------

    [Fact]
    public void Validate_NoErrors_Accepts()
    {
        Assert.True(PrivateCaCertificateValidator.Validate(_leaf, null, SslPolicyErrors.None, Roots(_ca)));
    }

    [Fact]
    public void Validate_NoCertificate_Rejects()
    {
        Assert.False(PrivateCaCertificateValidator.Validate(null, null, SslPolicyErrors.None, Roots(_ca)));
        Assert.False(PrivateCaCertificateValidator.Validate(_leaf, null, SslPolicyErrors.RemoteCertificateNotAvailable, Roots(_ca)));
    }

    [Fact]
    public void Validate_ChainErrorButSignedByTrustedCa_Accepts()
    {
        Assert.True(PrivateCaCertificateValidator.Validate(_leaf, null, SslPolicyErrors.RemoteCertificateChainErrors, Roots(_ca)));
    }

    [Fact]
    public void Validate_NameMismatch_RejectsEvenWhenChainIsTrusted()
    {
        // The bug the Kubernetes client had: chain fine, host name ignored.
        Assert.False(PrivateCaCertificateValidator.Validate(_leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch, Roots(_ca)));
        Assert.False(PrivateCaCertificateValidator.Validate(_leaf, null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, Roots(_ca)));
    }

    [Fact]
    public void Validate_SignedByOtherCa_Rejects()
    {
        Assert.False(PrivateCaCertificateValidator.Validate(_leafFromOtherCa, null, SslPolicyErrors.RemoteCertificateChainErrors, Roots(_ca)));
    }

    [Fact]
    public void Validate_ExpiredLeaf_Rejects()
    {
        Assert.False(PrivateCaCertificateValidator.Validate(_expiredLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors, Roots(_ca)));
    }

    [Fact]
    public void Validate_SelfSignedLeafNotInTrustStore_Rejects()
    {
        // A self-signed certificate is its own root; it must not "chain" just because it is.
        Assert.False(PrivateCaCertificateValidator.Validate(_otherCa, null, SslPolicyErrors.RemoteCertificateChainErrors, Roots(_ca)));
    }

    [Fact]
    public void Validate_EmptyTrustStore_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PrivateCaCertificateValidator.Validate(_leaf, null, SslPolicyErrors.None, []));
        Assert.Throws<ArgumentException>(() => PrivateCaCertificateValidator.CreateCallback([]));
    }

    // ---- LoadTrustedRoots ---------------------------------------------------------------------

    [Fact]
    public void LoadTrustedRoots_PemBundle_LoadsEveryCertificate()
    {
        var path = WritePem("bundle.crt", _ca, _otherCa);

        var roots = PrivateCaCertificateValidator.LoadTrustedRoots(path);

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, c => c.Thumbprint == _ca.Thumbprint);
    }

    [Fact]
    public void LoadTrustedRoots_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            PrivateCaCertificateValidator.LoadTrustedRoots(Path.Combine(_tempDir, "missing.crt")));
    }

    [Fact]
    public void LoadTrustedRoots_FileWithoutCertificate_Throws()
    {
        var path = Path.Combine(_tempDir, "empty.crt");
        File.WriteAllText(path, "not a certificate");

        Assert.Throws<InvalidOperationException>(() => PrivateCaCertificateValidator.LoadTrustedRoots(path));
    }

    // ---- Real TLS handshakes over loopback ----------------------------------------------------

    [Fact]
    public async Task Handshake_TrustedCaAndMatchingName_Succeeds()
    {
        await HandshakeAsync(_leaf, ServerName, PrivateCaCertificateValidator.CreateCallback(Roots(_ca)));
    }

    [Fact]
    public async Task Handshake_TrustedCaButWrongName_Fails()
    {
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            HandshakeAsync(_leaf, "evil.example", PrivateCaCertificateValidator.CreateCallback(Roots(_ca))));
    }

    [Fact]
    public async Task Handshake_CertificateFromUnknownCa_Fails()
    {
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            HandshakeAsync(_leafFromOtherCa, ServerName, PrivateCaCertificateValidator.CreateCallback(Roots(_ca))));
    }

    [Fact]
    public async Task Handshake_PrivateCaWithoutCallback_FailsWithDefaultValidation()
    {
        // The "no CA configured" default: the system trust store does not know the private CA.
        await Assert.ThrowsAsync<AuthenticationException>(() => HandshakeAsync(_leaf, ServerName, callback: null));
    }

    // ---- Kubernetes API client ----------------------------------------------------------------

    [Fact]
    public async Task KubernetesHandler_TrustedCaAndMatchingName_Succeeds()
    {
        using var handler = KubernetesWorkerReplicaCountProvider.CreateHandler(Roots(_ca));
        using var client = new HttpClient(handler);

        var body = await HttpsGetAsync(client, _leafWithIp);

        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task KubernetesHandler_NameMismatch_Fails()
    {
        // _leaf is valid only for the Redis DNS name, the client connects to 127.0.0.1 - the old
        // callback accepted this because the chain to the cluster CA was fine.
        using var handler = KubernetesWorkerReplicaCountProvider.CreateHandler(Roots(_ca));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpsGetAsync(client, _leaf));
    }

    [Fact]
    public async Task KubernetesHandler_UnknownCa_Fails()
    {
        using var handler = KubernetesWorkerReplicaCountProvider.CreateHandler(Roots(_otherCa));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpsGetAsync(client, _leafWithIp));
    }

    // ---- RedisTlsConfiguration ----------------------------------------------------------------

    [Fact]
    public void RedisApply_TlsOff_IgnoresTlsKeys()
    {
        var redis = StackExchange.Redis.ConfigurationOptions.Parse("localhost:6379");
        var sslHostBefore = redis.SslHost;
        var cache = new CacheOptions { TlsCaCertificatePath = Path.Combine(_tempDir, "missing.crt"), TlsHost = ServerName };

        var callback = RedisTlsConfiguration.Apply(redis, cache);

        Assert.Null(callback);
        Assert.Equal(sslHostBefore, redis.SslHost);
    }

    [Fact]
    public void RedisApply_TlsOnWithoutCa_KeepsDefaultValidation()
    {
        var redis = StackExchange.Redis.ConfigurationOptions.Parse("localhost:6380,ssl=true");
        var sslHostBefore = redis.SslHost;

        var callback = RedisTlsConfiguration.Apply(redis, new CacheOptions());

        Assert.Null(callback);
        Assert.Equal(sslHostBefore, redis.SslHost);
    }

    [Fact]
    public void RedisApply_TlsOnWithCaAndHost_InstallsValidatorAndSslHost()
    {
        var redis = StackExchange.Redis.ConfigurationOptions.Parse("redis-cluster-0.redis-cluster:6380,ssl=true");
        var cache = new CacheOptions { TlsCaCertificatePath = WritePem("ca.crt", _ca), TlsHost = $" {ServerName} " };

        var callback = RedisTlsConfiguration.Apply(redis, cache);

        Assert.NotNull(callback);
        Assert.Equal(ServerName, redis.SslHost);
        Assert.True(callback(this, _leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(callback(this, _leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(callback(this, _leafFromOtherCa, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void RedisApply_TlsOnWithMissingCaFile_FailsAtStartup()
    {
        var redis = StackExchange.Redis.ConfigurationOptions.Parse("localhost:6380,ssl=true");
        var cache = new CacheOptions { TlsCaCertificatePath = Path.Combine(_tempDir, "missing.crt") };

        Assert.Throws<FileNotFoundException>(() => RedisTlsConfiguration.Apply(redis, cache));
    }

    // ---- Add-on clients (AddonTlsConfiguration) -----------------------------------------------

    [Fact]
    public void AddonTls_Default_KeepsPlainHttpClient()
    {
        // Nothing configured = the behaviour before AddonTls existed: no custom validation.
        var trust = new AddonTlsTrust(TestOptions.For<AddonTlsOptions>());

        Assert.Null(trust.ValidationCallback);
        using var client = trust.CreateHttpClient(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }

    [Fact]
    public void AddonTls_Registration_BindsKeysAndValidatesOnAccess()
    {
        var options = TestOptions.For<AddonTlsOptions>(
            ("AddonTls:CaCertificatePath", WritePem("addon-ca.crt", _ca)), ("AddonTls:RequireHttps", "true"));
        Assert.True(options.Value.RequireHttps);

        var broken = TestOptions.For<AddonTlsOptions>(
            ("AddonTls:RequireHttps", "true"), ("StudyLifeWebhooks:BaseUrl", "http://studylife-webhooks:8000"));
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => broken.Value);
        Assert.Contains("StudyLifeWebhooks:BaseUrl", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddonTls_WithCa_TrustedCaAndMatchingName_Succeeds()
    {
        var callback = AddonTlsConfiguration.CreateValidationCallback(new AddonTlsOptions { CaCertificatePath = WritePem("addon-ca.crt", _ca) });
        using var client = AddonTlsConfiguration.CreateHttpClient(callback);

        Assert.Equal("ok", await HttpsGetAsync(client, _leafWithIp));
    }

    [Fact]
    public async Task AddonTls_WithCa_NameMismatch_Fails()
    {
        var callback = AddonTlsConfiguration.CreateValidationCallback(new AddonTlsOptions { CaCertificatePath = WritePem("addon-ca.crt", _ca) });
        using var client = AddonTlsConfiguration.CreateHttpClient(callback);

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpsGetAsync(client, _leaf));
    }

    [Fact]
    public async Task AddonTls_WithCa_CertificateFromOtherCa_Fails()
    {
        var callback = AddonTlsConfiguration.CreateValidationCallback(new AddonTlsOptions { CaCertificatePath = WritePem("addon-ca.crt", _ca) });
        using var client = AddonTlsConfiguration.CreateHttpClient(callback);
        using var otherLeaf = CreateLeaf(_otherCa, "localhost", IPAddress.Loopback, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpsGetAsync(client, otherLeaf));
    }

    [Fact]
    public async Task AddonTls_WithoutCa_PrivateCaCertificate_FailsWithDefaultValidation()
    {
        using var client = AddonTlsConfiguration.CreateHttpClient(validationCallback: null);

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpsGetAsync(client, _leafWithIp));
    }

    [Fact]
    public void AddonTlsValidator_DefaultsWithHttpUrls_Succeeds()
    {
        var validator = Validator(ai: "http://studylife-ai:8000", webhooks: "http://studylife-webhooks:8000", developers: "http://studylife-developers:8080");

        Assert.True(validator.Validate(null, new AddonTlsOptions()).Succeeded);
    }

    [Fact]
    public void AddonTlsValidator_MissingCaFile_FailsAtStartup()
    {
        var validator = Validator(ai: null, webhooks: null, developers: null);

        var result = validator.Validate(null, new AddonTlsOptions { CaCertificatePath = Path.Combine(_tempDir, "missing.crt") });

        Assert.True(result.Failed);
        Assert.Contains("AddonTls:CaCertificatePath", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AddonTlsValidator_RequireHttps_RejectsEveryPlainHttpUrl()
    {
        var validator = Validator(ai: "https://studylife-ai:8443", aiInternal: "http://studylife-ai:8001",
            webhooks: "http://studylife-webhooks:8000", developers: "https://studylife-developers:8443");

        var result = validator.Validate(null, new AddonTlsOptions { RequireHttps = true });

        Assert.True(result.Failed);
        Assert.Contains("StudyLifeAi:InternalBaseUrl", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("StudyLifeWebhooks:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("StudyLifeAi:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("StudyLifeDevelopers:BaseUrl", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AddonTlsValidator_RequireHttps_AllHttpsOrUnset_Succeeds()
    {
        var validator = Validator(ai: "https://studylife-ai:8443", aiInternal: "https://studylife-ai:8444", webhooks: null, developers: "");

        Assert.True(validator.Validate(null, new AddonTlsOptions { RequireHttps = true, CaCertificatePath = WritePem("addon-ca.crt", _ca) }).Succeeded);
    }

    [Fact]
    public void AddonTlsValidator_RequireHttps_RejectsRelativeOrMalformedUrl()
    {
        var validator = Validator(ai: "studylife-ai:8000", webhooks: null, developers: null);

        Assert.True(validator.Validate(null, new AddonTlsOptions { RequireHttps = true }).Failed);
    }

    private static AddonTlsOptionsValidator Validator(string? ai, string? webhooks, string? developers, string? aiInternal = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new StudyLifeAiOptions { BaseUrl = ai, InternalBaseUrl = aiInternal }),
            Microsoft.Extensions.Options.Options.Create(new StudyLifeWebhooksOptions { BaseUrl = webhooks }),
            Microsoft.Extensions.Options.Options.Create(new StudyLifeDevelopersOptions { BaseUrl = developers }));

    // ---- helpers ------------------------------------------------------------------------------

    private static X509Certificate2Collection Roots(params X509Certificate2[] certs)
    {
        var roots = new X509Certificate2Collection();
        foreach (var cert in certs)
            roots.Add(X509CertificateLoader.LoadCertificate(cert.RawData));
        return roots;
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

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca, string dnsName, IPAddress? ip,
        DateTimeOffset notBefore, DateTimeOffset notAfter)
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
        using var issued = request.Create(ca, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Round trip through PKCS#12 so the key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    private static async Task HandshakeAsync(X509Certificate2 serverCert, string targetHost,
        RemoteCertificateValidationCallback? callback)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cts.Token);
            await using var server = new SslStream(socket.GetStream());
            try
            {
                await server.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                }, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The client aborting the handshake is the expected outcome of the failure tests.
            }
        }, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var ssl = new SslStream(client.GetStream());
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                RemoteCertificateValidationCallback = callback,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cts.Token);
        }
        finally
        {
            client.Close();
            await serverTask;
        }
    }

    private static async Task<string> HttpsGetAsync(HttpClient client, X509Certificate2 serverCert)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cts.Token);
            await using var server = new SslStream(socket.GetStream());
            try
            {
                await server.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                }, cts.Token);
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await server.ReadAsync(buffer, cts.Token);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                await server.WriteAsync(
                    Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Client rejected the certificate - expected in the failure tests.
            }
        }, cts.Token);

        try
        {
            return await client.GetStringAsync(new Uri($"https://127.0.0.1:{port}/"), cts.Token);
        }
        finally
        {
            await serverTask;
        }
    }
}
