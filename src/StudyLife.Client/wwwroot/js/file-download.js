// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

// The browser half of BrowserFileDownloadService (IFileDownloadService): the bytes arrive as a
// DotNetStreamReference, become a blob URL, and a temporary <a download> starts the browser's
// normal file download. Browser only - the native app registers its own IFileDownloadService
// (system save/share sheet) and never loads this module.
export async function saveFile(fileName, contentStreamReference) {
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
