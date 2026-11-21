#include "cod2_platform.h"
#if COD2_WINDOWS_NATIVE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <ctype.h>
#include <errno.h>
#include <stdlib.h>
#include "win_paths.h"

static wchar_t *dataRoot, *userRoot;
static int dataCanonical;
static char *exePath, *exeDirectory;
static const wchar_t retailKey[] = L"SOFTWARE\\Activision\\Call of Duty 2";

char *Win_UTF8(const wchar_t *text)
{
    int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, -1, NULL, 0, NULL, NULL);
    char *result = size > 0 ? malloc(size) : NULL;
    if (result)
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, -1, result, size, NULL, NULL);
    return result;
}

wchar_t *Win_UTF16(const char *text)
{
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, NULL, 0);
    wchar_t *result = size > 0 ? malloc(size * sizeof(*result)) : NULL;
    if (result)
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, -1, result, size);
    return result;
}

static wchar_t *Join(const wchar_t *root, const wchar_t *relative)
{
    size_t left = wcslen(root), right = wcslen(relative);
    wchar_t *path;
    while (left && root[left - 1] == L'\\')
        --left;
    if (left + right + 2 > WIN_PATH_CAPACITY)
        return NULL;
    path = malloc((left + right + 2) * sizeof(*path));
    if (path) {
        memcpy(path, root, left * sizeof(*path));
        path[left] = L'\\';
        memcpy(path + left + 1, relative, (right + 1) * sizeof(*path));
    }
    return path;
}

/* Normalize before adding the extended prefix. This also accepts Wine's
 * drive-root-relative /data when a server is started from the Z: drive. */
wchar_t *Win_ExtendedPath(const wchar_t *path)
{
    wchar_t *input, *full, *result = NULL;
    DWORD size, length;
    if (!path || !*path)
        return NULL;
    input = _wcsdup(path);
    if (!input)
        return NULL;
    for (wchar_t *p = input; *p; ++p)
        if (*p == L'/') *p = L'\\';
    if (!wcsncmp(input, L"\\\\?\\UNC\\", 8)) {
        input[6] = L'\\';
        memmove(input, input + 6, (wcslen(input + 6) + 1) * sizeof(*input));
    } else if (!wcsncmp(input, L"\\\\?\\", 4)) {
        if (wcslen(input) < 7 || input[5] != L':' || input[6] != L'\\') goto done;
        memmove(input, input + 4, (wcslen(input + 4) + 1) * sizeof(*input));
    }
    if (!wcsncmp(input, L"\\\\.\\", 4)) goto done;
    /* Extended absolute paths never depend on the machine’s long-path policy. */
    if (wcslen(input) >= 3 && ((input[1] == L':' && input[2] == L'\\') ||
        (input[0] == L'\\' && input[1] == L'\\'))) {
        size_t length = wcslen(input);
        int unc = input[0] == L'\\';
        while (length > 3 && input[length - 1] == L'\\') input[--length] = 0;
        for (const wchar_t *p = input + (unc ? 2 : 3); *p;) {
            size_t count = wcscspn(p, L"\\");
            if (!count || p[count - 1] == L'.' || p[count - 1] == L' ' ||
                wcscspn(p, L":<>\"|?*") < count) goto done;
            p += count + (p[count] != 0);
        }
        result = Join(unc ? L"\\\\?\\UNC" : L"\\\\?", input + (unc ? 2 : 0));
        goto done;
    }
    size = GetFullPathNameW(input, 0, NULL, NULL);
    if (!size || size > WIN_PATH_CAPACITY - 8) goto done;
    full = malloc(size * sizeof(*full));
    if (!full) goto done;
    length = GetFullPathNameW(input, size, full, NULL);
    if (length && length < size) {
        while (length > 3 && full[length - 1] == L'\\') full[--length] = 0;
        if (full[0] == L'\\' && full[1] == L'\\')
            result = Join(L"\\\\?\\UNC", full + 2);
        else if (full[1] == L':' && full[2] == L'\\')
            result = Join(L"\\\\?", full);
    }
    free(full);
done:
    free(input);
    return result;
}

static wchar_t *CanonicalDirectory(const wchar_t *path)
{
    HANDLE handle = CreateFileW(path, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS, NULL);
    wchar_t *result = NULL;
    if (handle != INVALID_HANDLE_VALUE) {
        DWORD size = GetFinalPathNameByHandleW(handle, NULL, 0, FILE_NAME_NORMALIZED);
        if (size && size <= WIN_PATH_CAPACITY) {
            result = malloc(size * sizeof(*result));
            if (result) {
                DWORD length = GetFinalPathNameByHandleW(handle, result, size, FILE_NAME_NORMALIZED);
                if (!length || length >= size) { free(result); result = NULL; }
            }
        }
        CloseHandle(handle);
    }
    return result;
}

static int SameOrBelow(const wchar_t *path, const wchar_t *root)
{
    size_t length = wcslen(root);
    return wcslen(path) >= length &&
        CompareStringOrdinal(path, length, root, length, TRUE) == CSTR_EQUAL &&
        (path[length] == 0 || path[length] == L'\\' || root[length - 1] == L'\\');
}

static int ValidData(const wchar_t *path)
{
    if (!path) return 0;
    for (int i = 0; i < 16; ++i) {
        wchar_t name[24];
        swprintf(name, sizeof(name) / sizeof(*name), L"main\\iw_%02d.iwd", i);
        wchar_t *archive = Join(path, name);
        HANDLE file = archive ? CreateFileW(archive, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL, NULL) : INVALID_HANDLE_VALUE;
        free(archive);
        if (file == INVALID_HANDLE_VALUE) return 0;
        CloseHandle(file);
    }
    return 1;
}

static int SetData(const wchar_t *path)
{
    wchar_t *extended = Win_ExtendedPath(path);
    if (!ValidData(extended)) {
        free(extended);
        return 0;
    }
    dataRoot = CanonicalDirectory(extended);
    dataCanonical = dataRoot != NULL;
    if (dataCanonical) free(extended);
    else dataRoot = extended;
    return 1;
}

static wchar_t *ReadPath(HKEY root, const wchar_t *key, const wchar_t *name, DWORD view)
{
    HKEY handle;
    DWORD bytes = 0;
    DWORD type = 0;
    DWORD flags = RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND;
    wchar_t *path = NULL;
    if (RegOpenKeyExW(root, key, 0, KEY_QUERY_VALUE | view, &handle) != ERROR_SUCCESS)
        return NULL;
    if (RegGetValueW(handle, NULL, name, flags, NULL, NULL, &bytes) == ERROR_SUCCESS &&
        bytes >= sizeof(wchar_t) && bytes <= WIN_PATH_CAPACITY * sizeof(wchar_t)) {
        path = malloc(bytes);
        if (path && RegGetValueW(handle, NULL, name, flags, &type, path, &bytes) != ERROR_SUCCESS) {
            free(path);
            path = NULL;
        }
    }
    RegCloseKey(handle);
    if (path && type == REG_EXPAND_SZ) {
        DWORD length = ExpandEnvironmentStringsW(path, NULL, 0);
        wchar_t *expanded = length && length <= WIN_PATH_CAPACITY ? malloc(length * sizeof(*expanded)) : NULL;
        if (expanded) {
            DWORD actual = ExpandEnvironmentStringsW(path, expanded, length);
            if (!actual || actual > length) { free(expanded); expanded = NULL; }
        }
        free(path);
        path = expanded;
    }
    return path;
}

/* Steam's text KeyValues files have quoted strings, braces and // comments.
 * Keep brace depth so an app entry cannot be mistaken for a library path. */
static int VdfToken(char **cursor, char *out, size_t capacity)
{
    char *p = *cursor;
    size_t used = 0;
    int quoted;
    for (;;) {
        while (isspace((unsigned char)*p)) ++p;
        if (p[0] != '/' || p[1] != '/') break;
        while (*p && *p != '\n') ++p;
    }
    if (*p == '{' || *p == '}') {
        *cursor = p + 1;
        return *p;
    }
    quoted = *p == '"';
    if (quoted) ++p;
    while (*p && (quoted ? *p != '"' : !isspace((unsigned char)*p) && *p != '{' && *p != '}')) {
        if (quoted && p[0] == '\\' && (p[1] == '\\' || p[1] == '"')) ++p;
        if (used + 1 >= capacity) return 0;
        out[used++] = *p++;
    }
    if ((quoted && *p != '"') || (!quoted && !used)) return 0;
    out[used] = 0;
    *cursor = p + quoted;
    return 's';
}

static char *ReadVdf(const wchar_t *root, const wchar_t *name)
{
    wchar_t *path = Join(root, name);
    FILE *file = path ? _wfopen(path, L"rb") : NULL;
    char *text = NULL;
    free(path);
    if (!file) return NULL;
    if (!fseek(file, 0, SEEK_END)) {
        long length = ftell(file);
        if (length > 0 && length <= 1024 * 1024 && !fseek(file, 0, SEEK_SET)) {
            text = malloc(length + 1);
            if (text && fread(text, 1, length, file) == (size_t)length) text[length] = 0;
            else { free(text); text = NULL; }
        }
    }
    fclose(file);
    return text;
}

static char *SkipBom(char *text)
{
    return strncmp(text, "\xef\xbb\xbf", 3) ? text : text + 3;
}

static int SteamLibrary(const wchar_t *library)
{
    wchar_t *root = Win_ExtendedPath(library);
    char *text = root ? ReadVdf(root, L"steamapps\\appmanifest_2630.acf") : NULL;
    char token[1024], key[32], install[1024] = "";
    int depth = 0, app = 0, kind;
    if (text) {
        char *p = SkipBom(text);
        if (VdfToken(&p, token, sizeof(token)) == 's' && !_stricmp(token, "AppState")) {
            key[0] = 0;
            while ((kind = VdfToken(&p, token, sizeof(token))) != 0) {
                if (kind == '{') ++depth;
                else if (kind == '}' && --depth <= 0) break;
                else if (kind == 's' && depth == 1) {
                    if (!key[0]) snprintf(key, sizeof(key), "%.31s", token);
                    else {
                        if (!_stricmp(key, "appid")) app = !strcmp(token, "2630");
                        if (!_stricmp(key, "installdir")) strcpy(install, token);
                        key[0] = 0;
                    }
                    continue;
                }
                key[0] = 0;
            }
        }
    }
    if (app && depth == 0 && install[0] && !strpbrk(install, "/\\:") &&
        strcmp(install, ".") && strcmp(install, "..")) {
        wchar_t *common = Join(root, L"steamapps\\common");
        wchar_t *name = Win_UTF16(install);
        wchar_t *data = common && name ? Join(common, name) : NULL;
        SetData(data);
        free(common);
        free(name);
        free(data);
    }
    free(text);
    free(root);
    return dataRoot != NULL;
}

static void SteamRoot(const wchar_t *steam)
{
    wchar_t *root = Win_ExtendedPath(steam);
    char *text, *p, *token;
    char key[32] = "";
    int depth = 0, kind;
    if (!root || SteamLibrary(root)) { free(root); return; }
    text = ReadVdf(root, L"steamapps\\libraryfolders.vdf");
    if (!text) text = ReadVdf(root, L"config\\libraryfolders.vdf");
    free(root);
    token = malloc(4 * WIN_PATH_CAPACITY);
    if (!text || !token) goto done;
    p = SkipBom(text);
    if (VdfToken(&p, token, 4 * WIN_PATH_CAPACITY) != 's' || _stricmp(token, "libraryfolders")) goto done;
    while ((kind = VdfToken(&p, token, 4 * WIN_PATH_CAPACITY)) != 0) {
        if (kind == '{') ++depth;
        else if (kind == '}' && --depth <= 0) break;
        else if (kind == 's' && (depth == 1 || depth == 2)) {
            if (!key[0]) snprintf(key, sizeof(key), "%.31s", token);
            else {
                if ((depth == 2 && !_stricmp(key, "path")) ||
                    (depth == 1 && key[strspn(key, "0123456789")] == 0)) {
                    wchar_t *library = Win_UTF16(token);
                    int found = library && SteamLibrary(library);
                    free(library);
                    if (found) break;
                }
                key[0] = 0;
            }
            continue;
        }
        key[0] = 0;
    }
done:
    free(token);
    free(text);
}

static const char *Discover(void)
{
    static const DWORD views[] = { KEY_WOW64_32KEY, KEY_WOW64_64KEY };
    wchar_t *steam = ReadPath(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath", 0);
    if (steam) { SteamRoot(steam); free(steam); }
    for (size_t i = 0; i < 2 && !dataRoot; ++i) {
        steam = ReadPath(HKEY_LOCAL_MACHINE, L"Software\\Valve\\Steam", L"InstallPath", views[i]);
        if (steam) { SteamRoot(steam); free(steam); }
    }
    if (dataRoot) return "Steam";
    for (size_t i = 0; i < 2; ++i) {
        wchar_t *retail = ReadPath(HKEY_LOCAL_MACHINE, retailKey, L"InstallPath", views[i]);
        int found = SetData(retail);
        free(retail);
        if (found) return i == 0 ? "retail registry (32-bit)" : "retail registry (64-bit)";
    }
    wchar_t *folder = Win_UTF16(Win_ExecutableDirectory());
    int found = SetData(folder);
    free(folder);
    return found ? "executable folder" : NULL;
}

const char *Win_ExecutablePath(void)
{
    if (!exePath) {
        wchar_t *path = malloc(WIN_PATH_CAPACITY * sizeof(*path));
        if (path) {
            DWORD size = GetModuleFileNameW(NULL, path, WIN_PATH_CAPACITY);
            if (size && size < WIN_PATH_CAPACITY) exePath = Win_UTF8(path);
            free(path);
        }
    }
    return exePath ? exePath : "";
}

const char *Win_ExecutableDirectory(void)
{
    if (!exeDirectory) {
        exeDirectory = strdup(Win_ExecutablePath());
        if (exeDirectory) {
            char *slash = strrchr(exeDirectory, '\\');
            if (slash) slash[slash == exeDirectory + 2 ? 1 : 0] = 0;
        }
    }
    return exeDirectory ? exeDirectory : "";
}

char *Win_CommandLine(void)
{
    const wchar_t *tail = GetCommandLineW();
    int quoted = 0;
    while (*tail && (quoted || (*tail != L' ' && *tail != L'\t'))) {
        if (*tail == L'"') quoted = !quoted;
        ++tail;
    }
    while (*tail == L' ' || *tail == L'\t') ++tail;
    return Win_UTF8(tail);
}

static int PathOption(char *command, char **data, char **home)
{
    char *option, **output, *value, *end;
    command += strspn(command, " \t");
    if (_strnicmp(command, "set", 3) || !isspace((unsigned char)command[3])) return 0;
    option = command + 3 + strspn(command + 3, " \t");
    end = option + strcspn(option, " \t");
    if (end - option == 11 && !_strnicmp(option, "fs_basepath", 11)) output = data;
    else if (end - option == 11 && !_strnicmp(option, "fs_homepath", 11)) output = home;
    else return 0;
    value = end + strspn(end, " \t");
    end = value + strlen(value);
    while (end > value && (end[-1] == ' ' || end[-1] == '\t')) --end;
    if (*value == '"') {
        if (end - value < 2 || end[-1] != '"') return -1;
        ++value;
        --end;
    }
    if (end == value || memchr(value, '"', end - value)) return -1;
    free(*output);
    *output = malloc(end - value + 1);
    if (!*output) return -1;
    memcpy(*output, value, end - value);
    (*output)[end - value] = 0;
    return 1;
}

static int ExtractPaths(char *arguments, char **data, char **home)
{
    char *write = arguments;
    char *start = arguments;
    int quoted = 0;
    for (char *p = arguments; ; ++p) {
        if (*p == '"' && (p == arguments || p[-1] != '\\')) quoted = !quoted;
        if (*p && (*p != '+' || quoted)) continue;
        if (start != p) {
            char next = *p;
            *p = 0;
            int option = *start == '+' ? PathOption(start + 1, data, home) : 0;
            *p = next;
            if (option < 0) return 0;
            if (!option) {
                memmove(write, start, p - start);
                write += p - start;
            }
        }
        if (!*p) break;
        start = p;
    }
    *write = 0;
    return !quoted;
}

static int DirectoryId(const wchar_t *path, BY_HANDLE_FILE_INFORMATION *id)
{
    HANDLE directory = CreateFileW(path, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS, NULL);
    if (directory == INVALID_HANDLE_VALUE) return 0;
    int ok = GetFileInformationByHandle(directory, id) && (id->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY);
    CloseHandle(directory);
    return ok;
}

/* Some filesystem providers cannot return a normalized name for an open
 * directory. Compare directory identities when relying on the opened path. */
static int ContainsDirectory(const wchar_t *parent, const wchar_t *child)
{
    BY_HANDLE_FILE_INFORMATION root, current;
    if (!DirectoryId(parent, &root)) return 0;
    wchar_t *path = _wcsdup(child);
    if (!path) return 1;
    int found = 0;
    while (wcslen(path) >= 7) {
        if (DirectoryId(path, &current) && current.dwVolumeSerialNumber == root.dwVolumeSerialNumber &&
            current.nFileIndexHigh == root.nFileIndexHigh && current.nFileIndexLow == root.nFileIndexLow) {
            found = 1;
            break;
        }
        wchar_t *slash = wcsrchr(path, L'\\');
        if (!slash || slash <= path + 5) break;
        if (slash == path + 6) {
            if (!slash[1]) break;
            slash[1] = 0;
        } else {
            *slash = 0;
        }
    }
    free(path);
    return found;
}

static int OverlapsData(const wchar_t *home, int homeCanonical)
{
    if (SameOrBelow(home, dataRoot) || SameOrBelow(dataRoot, home)) return 1;
    if (homeCanonical && dataCanonical) return 0;
    return ContainsDirectory(dataRoot, home) || ContainsDirectory(home, dataRoot);
}

/* Create each component individually and refuse links below LocalAppData.
 * The known folder itself can be redirected by Windows. */
static int CheckDirectory(wchar_t *path, size_t start, int create)
{
    for (wchar_t *p = path + start; ; ++p) {
        if (*p && *p != L'\\') continue;
        wchar_t next = *p;
        *p = 0;
        DWORD attributes = GetFileAttributesW(path);
        if (attributes == INVALID_FILE_ATTRIBUTES && create &&
            (CreateDirectoryW(path, NULL) || GetLastError() == ERROR_ALREADY_EXISTS))
            attributes = GetFileAttributesW(path);
        *p = next;
        if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY) ||
            (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return 0;
        if (!next) return 1;
    }
}

static int SetHome(const char *override)
{
    PWSTR known = NULL;
    wchar_t *local, *canonical, *app, *requested, *wide;
    wchar_t *store;
    int ok = 0;
    if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_CREATE, NULL, &known))) {
        CoTaskMemFree(known);
        known = NULL;
        if (FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData, KF_FLAG_DONT_VERIFY, NULL, &known))) {
            CoTaskMemFree(known);
            return 0;
        }
    }
    local = Win_ExtendedPath(known);
    CoTaskMemFree(known);
    canonical = local ? CanonicalDirectory(local) : NULL;
    int homeCanonical = canonical != NULL;
    if (!canonical && local) canonical = _wcsdup(local);
    app = canonical ? Join(canonical, L"CoD2 Unbound") : NULL;
    store = canonical ? Join(canonical, L"VirtualStore") : NULL;
    wide = override ? Win_UTF16(override) : NULL;
    requested = override ? Win_ExtendedPath(wide) : (app ? _wcsdup(app) : NULL);
    /* Compare with the canonical root, including when LocalAppData is redirected. */
    if (override && requested && local && canonical && !SameOrBelow(requested, canonical) &&
        wcslen(requested) > wcslen(local) && SameOrBelow(requested, local)) {
        wchar_t *mapped = Join(canonical, requested + wcslen(local) + 1);
        free(requested);
        requested = mapped;
    }
    if (requested && app && store && SameOrBelow(requested, app) && !SameOrBelow(requested, store) &&
        !OverlapsData(requested, homeCanonical) &&
        CheckDirectory(requested, wcslen(canonical) + 1, 1)) {
        HANDLE folder = CreateFileW(requested, FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, NULL);
        if (folder != INVALID_HANDLE_VALUE) {
            CloseHandle(folder);
            userRoot = requested;
            requested = NULL;
            ok = 1;
        }
    }
    free(wide);
    free(requested);
    free(app);
    free(store);
    free(local);
    free(canonical);
    return ok;
}

static int StartupError(const char *message)
{
    fprintf(stderr, "CoD2 Unbound: %s\n", message);
#ifndef DEDICATED
    if (!getenv("COD2_CRASH_NOPOPUP")) MessageBoxA(NULL, message, "CoD2 Unbound", MB_OK | MB_ICONERROR);
#endif
    return 0;
}

int Win_ConfigurePaths(char *arguments)
{
    char *data = NULL, *home = NULL;
    const char *source = NULL;
    if (!ExtractPaths(arguments, &data, &home)) {
        free(data);
        free(home);
        return StartupError("Invalid path option. Use +set fs_basepath followed by a quoted game folder.");
    }
    if (data) {
        wchar_t *path = Win_UTF16(data);
        if (SetData(path)) source = "command line";
        free(path);
    } else {
        source = Discover();
    }
    int explicitData = data != NULL;
    free(data);
    if (!source) {
        free(home);
        return StartupError(explicitData
            ? "The selected game folder must contain readable main/iw_00.iwd through main/iw_15.iwd. Check +set fs_basepath."
            : "Call of Duty 2 data was not found in Steam, the retail registry or beside the executable. Select the folder containing main/iw_00.iwd through main/iw_15.iwd with +set fs_basepath.");
    }
    int ok = SetHome(home);
    free(home);
    if (!ok) return StartupError("Could not open a writable game folder under LocalAppData\\CoD2 Unbound. Check that it is separate from the game data and that it is not a junction.");
    char *base = Win_UTF8(dataRoot), *user = Win_UTF8(userRoot);
    fprintf(stdout, "Game data (%s): %s\nUser data: %s\n", source, base ? base : "", user ? user : "");
    free(base);
    free(user);
    return 1;
}

static int LegacyState(const wchar_t *component, size_t length, int last)
{
    if (length == 7 && !_wcsnicmp(component, L"players", 7)) return 1;
    if (length == 5 && !_wcsnicmp(component, L"demos", 5)) return 1;
    if (!last) return 0;
    return (length >= 4 && !_wcsicmp(component + length - 4, L".cfg")) ||
        !_wcsicmp(component, L"servercache.dat") || !_wcsicmp(component, L"ban.txt");
}

wchar_t *Win_OSPath(const char *path, int writing)
{
    int data = !_strnicmp(path, WIN_DATA_ROOT, sizeof(WIN_DATA_ROOT) - 1) &&
        (!path[sizeof(WIN_DATA_ROOT) - 1] || strchr("/\\", path[sizeof(WIN_DATA_ROOT) - 1]));
    const wchar_t *root = data ? dataRoot : userRoot;
    wchar_t *relative, *result = NULL;
    if (!root || (writing && data)) goto invalid;
    if (data) path += sizeof(WIN_DATA_ROOT) - 1;
    else if (!_strnicmp(path, WIN_USER_ROOT, sizeof(WIN_USER_ROOT) - 1) &&
        (!path[sizeof(WIN_USER_ROOT) - 1] || strchr("/\\", path[sizeof(WIN_USER_ROOT) - 1])))
        path += sizeof(WIN_USER_ROOT) - 1;
    else if (strchr("/\\", *path) && *path) goto invalid;
    while (*path == '/' || *path == '\\') ++path;
    if (path[0] == '.' && (path[1] == '/' || path[1] == '\\')) path += 2;
    if (!strcmp(path, ".") || !*path) return _wcsdup(root);
    relative = Win_UTF16(path);
    if (!relative) goto invalid;
    for (wchar_t *p = relative; *p; ++p) if (*p == L'/') *p = L'\\';
    size_t length = wcslen(relative);
    while (length && relative[length - 1] == L'\\') relative[--length] = 0;
    for (wchar_t *p = relative; *p;) {
        size_t count = wcscspn(p, L"\\");
        if (!count || p[count - 1] == L'.' || p[count - 1] == L' ' ||
            wcscspn(p, L":<>\"|?*") < count || (data && LegacyState(p, count, !p[count]))) goto done;
        p += count + (p[count] != 0);
    }
    result = Join(root, relative);
    if (result && !data) {
        /* Existing descendants cannot redirect writes into an install or elsewhere. */
        for (wchar_t *p = result + wcslen(root) + 1; ; ++p) {
            if (*p && *p != L'\\') continue;
            wchar_t next = *p;
            *p = 0;
            DWORD attributes = GetFileAttributesW(result);
            *p = next;
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
                free(result);
                result = NULL;
                break;
            }
            if (!next || attributes == INVALID_FILE_ATTRIBUTES) break;
        }
    }
done:
    free(relative);
    if (result) return result;
invalid:
    errno = EACCES;
    return NULL;
}
#endif
