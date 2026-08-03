// Collocated JS isolation module for the passkey pages (same pattern as Focus.razor.js -
// index.html must not be touched). navigator.credentials.create()/get() are pure
// browser APIs with no C# equivalent; this module translates between the Base64url JSON from
// Fido2NetLib (server) and the ArrayBuffer fields of the WebAuthn API. Imported by Login.razor,
// Register.razor, AND PasskeyDeviceManager.razor - deliberately ONE module at this
// path instead of three copies of the same conversion logic.

function base64UrlToBuffer(value) {
    const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64 + '='.repeat((4 - base64.length % 4) % 4);
    const binary = atob(padded);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes.buffer;
}

function bufferToBase64Url(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    for (const b of bytes) binary += String.fromCharCode(b);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function isPasskeySupported() {
    return !!(navigator.credentials && window.PublicKeyCredential);
}

// optionsJson: CredentialCreateOptions.ToJson() from Fido2NetLib (Base64url strings for all
// byte fields). Return value: JSON string in the format of AuthenticatorAttestationRawResponse.
// Cancellation/rejection by the user throws (NotAllowedError) - the C# caller catches this
// as a JSException and shows a friendly error message.
export async function createPasskey(optionsJson) {
    const options = JSON.parse(optionsJson);
    options.challenge = base64UrlToBuffer(options.challenge);
    options.user.id = base64UrlToBuffer(options.user.id);
    if (Array.isArray(options.excludeCredentials)) {
        options.excludeCredentials = options.excludeCredentials.map(c => ({ ...c, id: base64UrlToBuffer(c.id) }));
    }
    // Fido2NetLib serializes unset optional fields as null - WebAuthn sometimes rejects
    // null values with a TypeError (e.g. authenticatorAttachment in Safari), so remove them.
    if (options.authenticatorSelection && options.authenticatorSelection.authenticatorAttachment == null) {
        delete options.authenticatorSelection.authenticatorAttachment;
    }
    if (options.extensions == null) delete options.extensions;

    const credential = await navigator.credentials.create({ publicKey: options });
    return JSON.stringify({
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
            attestationObject: bufferToBase64Url(credential.response.attestationObject),
            clientDataJSON: bufferToBase64Url(credential.response.clientDataJSON),
            // Fido2NetLib's model requires the field (non-nullable array) - older browsers without
            // getTransports() then just get an empty list.
            transports: credential.response.getTransports ? credential.response.getTransports() : [],
        },
        clientExtensionResults: credential.getClientExtensionResults(),
    });
}

// optionsJson: AssertionOptions.ToJson() from Fido2NetLib. Return value: JSON string in the
// format of AuthenticatorAssertionRawResponse.
export async function getPasskeyAssertion(optionsJson) {
    const options = JSON.parse(optionsJson);
    options.challenge = base64UrlToBuffer(options.challenge);
    if (Array.isArray(options.allowCredentials)) {
        options.allowCredentials = options.allowCredentials.map(c => ({ ...c, id: base64UrlToBuffer(c.id) }));
    }
    if (options.extensions == null) delete options.extensions;

    const credential = await navigator.credentials.get({ publicKey: options });
    return JSON.stringify({
        id: credential.id,
        rawId: bufferToBase64Url(credential.rawId),
        type: credential.type,
        response: {
            authenticatorData: bufferToBase64Url(credential.response.authenticatorData),
            clientDataJSON: bufferToBase64Url(credential.response.clientDataJSON),
            signature: bufferToBase64Url(credential.response.signature),
            userHandle: credential.response.userHandle ? bufferToBase64Url(credential.response.userHandle) : null,
        },
        clientExtensionResults: credential.getClientExtensionResults(),
    });
}
