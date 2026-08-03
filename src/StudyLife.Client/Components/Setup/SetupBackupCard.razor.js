// Collocated JS isolation module for SetupBackupCard.razor - index.html is intentionally
// left untouched, hence here instead of a global <script> block - same pattern as
// Focus.razor.js.
//
// The encrypted DB download goes through POST with the password in the body (must not be in
// the URL, see the BackupController comment) and therefore, unlike the unencrypted
// download, cannot be delivered via a native <a download href="..."> Instead
// the Blazor component reads the server response as a stream and passes it here
// (DotNetStreamReference) - this helper builds a blob URL from it and triggers the
// browser download programmatically, exactly like a normal file download.
export async function downloadFileFromStream(fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName ?? '';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
}
