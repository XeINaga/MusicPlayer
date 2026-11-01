// MusicPlayer installer custom actions.
//
// NormalizeInstallDir: normalizes the chosen install directory so it always
// ends with "MusicPlayer". If the user picks a folder that does not already
// end with the software name (case-insensitive, ignoring a trailing
// backslash), we append "\MusicPlayer".
//
// BrowseInstallDir: shows the modern Windows folder picker (IFileDialog with
// FOS_PICKFOLDERS) and writes the chosen path back into INSTALLFOLDER —
// replaces WiX's ancient BrowseDlg tree as the wizard's Browse button.
//
// Compiled by build_msi.ps1 into a single normalize.dll with MinGW g++
// (-static, so no C/C++ runtime dependency — msiexec can load it on any
// target machine). Links against msi + ole32 + shell32 + user32.

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <msi.h>
#include <shobjidl.h>    // IFileDialog / IShellItem / FOS_*
#include <shlobj.h>      // SHCreateItemFromParsingName

// MinGW's msi.h does not prototype these, so declare them ourselves and link
// against -lmsi. Signatures match the Windows MSI API.
extern "C" UINT __stdcall MsiGetPropertyW(MSIHANDLE hInstall, LPCWSTR szName,
                                          LPWSTR szValueBuf, LPDWORD pcchValueBuf);
extern "C" UINT __stdcall MsiSetPropertyW(MSIHANDLE hInstall, LPCWSTR szName,
                                          LPCWSTR szValue);
extern "C" UINT __stdcall MsiSetTargetPathW(MSIHANDLE hInstall, LPCWSTR szFolder,
                                            LPCWSTR szFolderPath, DWORD dwFlags);

static int wendswith_ci(const wchar_t* str, const wchar_t* suffix) {
    int sl = lstrlenW(str);
    int pl = lstrlenW(suffix);
    if (pl > sl) return 0;
    int off = sl - pl;
    for (int i = 0; i < pl; i++) {
        wchar_t a = str[off + i];
        wchar_t b = suffix[i];
        if (a >= L'A' && a <= L'Z') a = (wchar_t)(a + 32);
        if (b >= L'A' && b <= L'Z') b = (wchar_t)(b + 32);
        if (a != b) return 0;
    }
    return 1;
}

// GUIDs come from the headers via __uuidof (compile-time constants) — hand
// copying CLSIDs into this file has already shipped one wrong digit once
// (REGDB_E_CLASSNOTREG at install time, invisible without a debugger).

// Show the modern Windows folder picker (IFileDialog, FOS_PICKFOLDERS) and
// write the chosen path back into INSTALLFOLDER. Returns ERROR_SUCCESS even
// when the user cancels — the property is simply left as it was. The wizard
// runs SetTargetPath right after this so the (already-costed) directory
// resolves to the new value.
extern "C" UINT __stdcall BrowseInstallDir(MSIHANDLE hInstall) {
    wchar_t cur[4096];
    DWORD len = 4096;
    bool haveCur = (MsiGetPropertyW(hInstall, L"INSTALLFOLDER", cur, &len) == ERROR_SUCCESS && len > 0);

    HRESULT hrInit = CoInitialize(nullptr); // wizard thread is already STA; S_FALSE is fine
    IFileDialog* dlg = nullptr;
    do {
        HRESULT hr = CoCreateInstance(__uuidof(FileOpenDialog), nullptr, CLSCTX_INPROC_SERVER,
                                      __uuidof(IFileDialog), (void**)&dlg);
        if (FAILED(hr))
            break;
        DWORD opts = 0;
        if (FAILED(dlg->GetOptions(&opts)))
            break;
        if (FAILED(dlg->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM)))
            break;
        dlg->SetTitle(L"Select the MusicPlayer install folder");
        dlg->SetOkButtonLabel(L"Select Folder");
        if (haveCur) {
            // Start in the current folder when it is a real path — not the
            // still-formatted "[ProgramFiles64Folder]MusicPlayer" default.
            bool formatted = false;
            for (DWORD i = 0; i < len && cur[i]; i++) {
                if (cur[i] == L'[') { formatted = true; break; }
            }
            if (!formatted) {
                IShellItem* folder = nullptr;
                if (SUCCEEDED(SHCreateItemFromParsingName(cur, nullptr, __uuidof(IShellItem),
                                                          (void**)&folder))) {
                    dlg->SetFolder(folder);
                    folder->Release();
                }
            }
        }
        // The action runs on a worker thread: GetActiveWindow is
        // thread-local and returns NULL there, so fall back to the
        // foreground wizard window to keep the picker modal to it.
        HWND owner = GetActiveWindow();
        if (owner == nullptr)
            owner = GetForegroundWindow();
        if (FAILED(dlg->Show(owner)))
            break; // user cancelled, or the dialog could not be shown
        IShellItem* res = nullptr;
        if (FAILED(dlg->GetResult(&res)))
            break;
        PWSTR path = nullptr;
        if (SUCCEEDED(res->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
            // MSI directory paths always carry a trailing backslash; the
            // picker returns a bare path.
            size_t pl = wcslen(path);
            wchar_t withSep[4096];
            if (pl > 0 && pl < 4094 && path[pl - 1] != L'\\') {
                wcscpy_s(withSep, path);
                withSep[pl] = L'\\';
                withSep[pl + 1] = L'\0';
            } else {
                wcscpy_s(withSep, path);
            }
            // Write into a plain scratch property — the wizard then moves it
            // into INSTALLFOLDER with a SetProperty control event, which
            // (unlike a CA property write) refreshes the bound PathEdit
            // immediately. A CA property write on a directory property also
            // used to kill the following SetTargetPath with 2732.
            MsiSetPropertyW(hInstall, L"BROWSED_PATH", withSep);
            CoTaskMemFree(path);
        }
        res->Release();
    } while (false);
    if (dlg)
        dlg->Release();
    if (hrInit == S_OK)
        CoUninitialize();
    return ERROR_SUCCESS;
}

extern "C" UINT __stdcall NormalizeInstallDir(MSIHANDLE hInstall) {
    wchar_t buf[4096];
    DWORD len = 4096;
    if (MsiGetPropertyW(hInstall, L"INSTALLFOLDER", buf, &len) != ERROR_SUCCESS) {
        return ERROR_SUCCESS; // never fail the install over this
    }

    // If this is still a formatted directory reference (e.g.
    // "[ProgramFiles64Folder]MusicPlayer"), the default is already correct;
    // leave it untouched to avoid corrupting the path.
    for (DWORD i = 0; i < len && buf[i] != L'\0'; i++) {
        if (buf[i] == L'[') return ERROR_SUCCESS;
    }

    // Trim a single trailing backslash.
    while (len > 0 && buf[len - 1] == L'\\') {
        buf[len - 1] = L'\0';
        len--;
    }
    if (len == 0) return ERROR_SUCCESS;

    const wchar_t* name = L"MusicPlayer";
    if (!wendswith_ci(buf, name)) {
        // Room check before appending: a property of up to 4095 chars fits the
        // MsiGetPropertyW buffer, but lstrcatW("\\MusicPlayer") on top of it
        // would write past the end of buf (stack overwrite inside the
        // elevated custom action). Too long → leave the property untouched,
        // same as every other bail-out here.
        if (len + 12 > 4096)
            return ERROR_SUCCESS;
        lstrcatW(buf, L"\\MusicPlayer");
        MsiSetPropertyW(hInstall, L"INSTALLFOLDER", buf);
    }
    return ERROR_SUCCESS;
}
