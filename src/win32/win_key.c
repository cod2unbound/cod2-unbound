#include "cod2_platform.h"
#if COD2_WINDOWS_NATIVE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincred.h>
#include "win_paths.h"

static wchar_t target[] = L"CoD2 Unbound/CD Key";

/* The same CRC-16 as CL_CDKeyValidate, also available to dedicated builds. */
static int ValidKey(const char *key)
{
    unsigned crc = 0;
    char checksum[5];
    if (key[20]) return 0;
    for (int i = 0; i < 16; ++i) {
        unsigned ch = (unsigned char)key[i];
        if (!(ch >= '0' && ch <= '9') && !(ch >= 'A' && ch <= 'Z') && !(ch >= 'a' && ch <= 'z'))
            return 0;
        crc ^= ch;
        for (int bit = 0; bit < 8; ++bit) crc = crc & 1 ? (crc >> 1) ^ 0xa001 : crc >> 1;
    }
    snprintf(checksum, sizeof(checksum), "%04x", crc);
    return !_stricmp(key + 16, checksum);
}

static int LegacyKey(char key[21])
{
    static const struct {
        HKEY root;
        const wchar_t *path;
        REGSAM view;
    } places[] = {
        { HKEY_CURRENT_USER, L"Software\\Classes\\VirtualStore\\MACHINE\\SOFTWARE\\WOW6432Node\\Activision\\Call of Duty 2", 0 },
        { HKEY_CURRENT_USER, L"Software\\Classes\\VirtualStore\\MACHINE\\SOFTWARE\\Activision\\Call of Duty 2", 0 },
        { HKEY_LOCAL_MACHINE, L"SOFTWARE\\Activision\\Call of Duty 2", KEY_WOW64_32KEY },
        { HKEY_LOCAL_MACHINE, L"SOFTWARE\\Activision\\Call of Duty 2", KEY_WOW64_64KEY },
    };
    for (size_t i = 0; i < sizeof(places) / sizeof(places[0]); ++i) {
        HKEY handle;
        DWORD type = 0, bytes = 21;
        if (RegOpenKeyExW(places[i].root, places[i].path, 0, KEY_QUERY_VALUE | places[i].view, &handle) != ERROR_SUCCESS)
            continue;
        LONG status = RegQueryValueExA(handle, "codkey", NULL, &type, (BYTE *)key, &bytes);
        RegCloseKey(handle);
        if (status == ERROR_SUCCESS && type == REG_SZ && bytes == 21 && ValidKey(key))
            return 1;
    }
    return 0;
}

int Win_ReadCDKey(char key[21])
{
    PCREDENTIALW entry;
    int valid = 0;
    if (CredReadW(target, CRED_TYPE_GENERIC, 0, &entry)) {
        if (entry->CredentialBlobSize == 20) {
            memcpy(key, entry->CredentialBlob, 20);
            key[20] = '\0';
            valid = ValidKey(key);
        }
        SecureZeroMemory(entry->CredentialBlob, entry->CredentialBlobSize);
        CredFree(entry);
        /* A broken explicit credential requires re-entry, even if retail has a key. */
        if (!valid) fprintf(stderr, "CoD2 Unbound: re-enter your CD key in the launcher.\n");
    } else if (GetLastError() == ERROR_NOT_FOUND) {
        valid = LegacyKey(key);
    } else {
        fprintf(stderr, "CoD2 Unbound: could not read the Windows CD key credential.\n");
    }
    if (!valid) SecureZeroMemory(key, 21);
    return valid;
}

int Win_WriteCDKey(const char key[21])
{
    CREDENTIALW entry = {0};
    if (!ValidKey(key)) return 0;
    entry.Type = CRED_TYPE_GENERIC;
    entry.TargetName = target;
    entry.CredentialBlobSize = 20;
    entry.CredentialBlob = (BYTE *)key;
    entry.Persist = CRED_PERSIST_LOCAL_MACHINE;
    if (CredWriteW(&entry, 0)) return 1;
    fprintf(stderr, "CoD2 Unbound: could not save the Windows CD key credential.\n");
    return 0;
}
#endif
