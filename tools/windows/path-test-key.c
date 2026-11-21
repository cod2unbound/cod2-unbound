/* Wine fixture helper. The two in-file values only exercise the CRC format. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincred.h>
#include <string.h>
#include <stdio.h>
#include "win32/win_paths.h"

static int PutLegacy(HKEY root, const char *path, REGSAM view, const char *value)
{
    HKEY key;
    if (RegCreateKeyExA(root, path, 0, NULL, 0, KEY_SET_VALUE | view, NULL, &key, NULL) != ERROR_SUCCESS)
        return 0;
    LONG status = RegSetValueExA(key, "codkey", 0, REG_SZ, (const BYTE *)value, (DWORD)strlen(value) + 1);
    RegCloseKey(key);
    return status == ERROR_SUCCESS;
}

int main(int argc, char **argv)
{
    static const char retail[] = "Software\\Activision\\Call of Duty 2";
    static const char shadow[] = "Software\\Classes\\VirtualStore\\MACHINE\\SOFTWARE\\WOW6432Node\\Activision\\Call of Duty 2";
    static const char oldKey[] = "11111111111111114D09";
    static const char newKey[] = "000000000000000086D3";
    static const char invalid[] = "11111111111111110000";
    char result[21];
    if (argc == 2 && !strcmp(argv[1], "verify")) {
        if (!Win_ReadCDKey(result) || strcmp(result, newKey)) return 5;
        HKEY key;
        DWORD size = sizeof(result);
        if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, retail, 0, KEY_QUERY_VALUE | KEY_WOW64_32KEY, &key) != ERROR_SUCCESS)
            return 6;
        LONG status = RegGetValueA(key, NULL, "codkey", RRF_RT_REG_SZ, NULL, result, &size);
        RegCloseKey(key);
        if (status != ERROR_SUCCESS || strcmp(result, oldKey)) return 6;
        size = sizeof(result);
        if (RegGetValueA(HKEY_CURRENT_USER, shadow, "codkey", RRF_RT_REG_SZ,
                         NULL, result, &size) != ERROR_SUCCESS || strcmp(result, invalid)) return 7;
        SecureZeroMemory(result, sizeof(result));
        puts("PASS engine preserved the credential and both legacy values");
        return 0;
    }
    if (!PutLegacy(HKEY_LOCAL_MACHINE, retail, KEY_WOW64_32KEY, oldKey) ||
        !PutLegacy(HKEY_CURRENT_USER, shadow, 0, invalid) ||
        !Win_ReadCDKey(result) || strcmp(result, oldKey)) return 1;
    if (!Win_WriteCDKey(newKey) || !Win_ReadCDKey(result) || strcmp(result, newKey)) return 2;

    /* A launcher writes the same generic credential directly via Credential Manager. */
    CREDENTIALW entry = {0};
    entry.Type = CRED_TYPE_GENERIC;
    entry.TargetName = L"CoD2 Unbound/CD Key";
    entry.CredentialBlob = (BYTE *)invalid;
    entry.CredentialBlobSize = 20;
    entry.Persist = CRED_PERSIST_LOCAL_MACHINE;
    if (!CredWriteW(&entry, 0) || Win_ReadCDKey(result)) return 3;
    entry.CredentialBlob = (BYTE *)newKey;
    if (!CredWriteW(&entry, 0) || !Win_ReadCDKey(result) || strcmp(result, newKey)) return 4;
    SecureZeroMemory(result, sizeof(result));
    puts("PASS legacy fallback, in-game save, invalid explicit credential and launcher precedence");
    return 0;
}
