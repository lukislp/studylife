using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyLife.Server.Tests;

/// <summary>
/// Simulated WebAuthn authenticator for the passkey tests (same pattern as FakePushKeys
/// in BackgroundTaskServiceTestHelpers): generates a real ECDSA P-256 key and uses it to
/// build standard-compliant attestation ("none" format) and assertion responses that
/// Fido2NetLib fully verifies cryptographically on the server side - no mocking of the
/// verification logic, the tests prove the real path.
/// </summary>
internal sealed class FakePasskey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);
    public string CredentialIdBase64Url => ToBase64Url(CredentialId);

    /// <summary>user.id from the registration options - the authenticator returns it
    /// as userHandle on login (discoverable credential behavior).</summary>
    public byte[]? UserHandle { get; private set; }

    public static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    /// <summary>
    /// Builds the AuthenticatorAttestationRawResponse for CredentialCreateOptions.ToJson():
    /// clientDataJSON (webauthn.create, challenge from the options, given origin) plus
    /// attestationObject (CBOR: fmt "none", empty attStmt, authData with COSE public key).
    /// </summary>
    public JsonNode CreateAttestationResponse(string optionsJson, string origin, uint signCount = 1)
    {
        using var options = JsonDocument.Parse(optionsJson);
        var challenge = FromBase64Url(options.RootElement.GetProperty("challenge").GetString()!);
        var rpId = options.RootElement.GetProperty("rp").GetProperty("id").GetString()!;
        UserHandle = FromBase64Url(options.RootElement.GetProperty("user").GetProperty("id").GetString()!);

        var clientDataJson = ClientDataJson("webauthn.create", challenge, origin);

        // authData: rpIdHash(32) | flags(1) | signCount(4, BE) | attestedCredentialData
        // flags 0x45 = UserPresent | UserVerified | AttestedCredentialDataIncluded
        var attestedCredentialData = BuildAttestedCredentialData();
        var authData = BuildAuthenticatorData(rpId, flags: 0x45, signCount, attestedCredentialData);

        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(authData);
        writer.WriteEndMap();
        var attestationObject = writer.Encode();

        return new JsonObject
        {
            ["id"] = CredentialIdBase64Url,
            ["rawId"] = CredentialIdBase64Url,
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["attestationObject"] = ToBase64Url(attestationObject),
                ["clientDataJSON"] = ToBase64Url(clientDataJson),
                // Required field for ASP.NET model validation (non-nullable array in the Fido2 model).
                ["transports"] = new JsonArray("internal"),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    /// <summary>
    /// Builds the AuthenticatorAssertionRawResponse for AssertionOptions.ToJson(). signCount
    /// controls the replay-protection test; signWith allows signing with a DIFFERENT key
    /// (valid structure, wrong signature -> server must respond with 401).
    /// </summary>
    public JsonNode CreateAssertionResponse(string optionsJson, string origin, uint signCount, FakePasskey? signWith = null)
    {
        using var options = JsonDocument.Parse(optionsJson);
        var challenge = FromBase64Url(options.RootElement.GetProperty("challenge").GetString()!);
        var rpId = options.RootElement.GetProperty("rpId").GetString()!;

        var clientDataJson = ClientDataJson("webauthn.get", challenge, origin);
        // flags 0x05 = UserPresent | UserVerified (no attested credential data on login)
        var authenticatorData = BuildAuthenticatorData(rpId, flags: 0x05, signCount, attestedCredentialData: null);

        var signedData = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signedData, 0);
        SHA256.HashData(clientDataJson).CopyTo(signedData.AsSpan(authenticatorData.Length));
        var signature = (signWith ?? this)._key.SignData(signedData, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = CredentialIdBase64Url,
            ["rawId"] = CredentialIdBase64Url,
            ["type"] = "public-key",
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = ToBase64Url(authenticatorData),
                ["clientDataJSON"] = ToBase64Url(clientDataJson),
                ["signature"] = ToBase64Url(signature),
                ["userHandle"] = UserHandle is null ? null : ToBase64Url(UserHandle),
            },
            ["clientExtensionResults"] = new JsonObject(),
        };
    }

    private static byte[] ClientDataJson(string type, byte[] challenge, string origin) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["type"] = type,
            ["challenge"] = ToBase64Url(challenge),
            ["origin"] = origin,
        });

    private static byte[] BuildAuthenticatorData(string rpId, byte flags, uint signCount, byte[]? attestedCredentialData)
    {
        using var ms = new MemoryStream();
        ms.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        ms.WriteByte(flags);
        Span<byte> counter = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(counter, signCount);
        ms.Write(counter);
        if (attestedCredentialData is not null) ms.Write(attestedCredentialData);
        return ms.ToArray();
    }

    // AAGUID(16, Null) | credIdLen(2, BE) | credId | COSE-Key (EC2/P-256/ES256)
    private byte[] BuildAttestedCredentialData()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[16]);
        ms.WriteByte((byte)(CredentialId.Length >> 8));
        ms.WriteByte((byte)(CredentialId.Length & 0xFF));
        ms.Write(CredentialId);
        ms.Write(BuildCoseKey());
        return ms.ToArray();
    }

    private byte[] BuildCoseKey()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);  // kty
        writer.WriteInt32(2);  // EC2
        writer.WriteInt32(3);  // alg
        writer.WriteInt32(-7); // ES256
        writer.WriteInt32(-1); // crv
        writer.WriteInt32(1);  // P-256
        writer.WriteInt32(-2); // x
        writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3); // y
        writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }

    public void Dispose() => _key.Dispose();
}
