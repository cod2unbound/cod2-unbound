/* Ordinary key lookup and save, for the disposable check-paths.py prefix only. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "win32/win_paths.h"

static const char retailKey[] = "11111111111111114D09";
static const char savedKey[] = "000000000000000086D3";
static const char invalidShadow[] = "11111111111111110000";

static const struct {
    HKEY root;
    const char *path;
    REGSAM view;
    const char *value;
} legacy[] = {
    { HKEY_LOCAL_MACHINE, "Software\\Activision\\Call of Duty 2", KEY_WOW64_32KEY, retailKey },
    { HKEY_CURRENT_USER, "Software\\Classes\\VirtualStore\\MACHINE\\SOFTWARE\\WOW6432Node\\Activision\\Call of Duty 2",
      0, invalidShadow },
};

static int PrivateWine(void)
{
    static const char original[] = "/out/original-key-legacy/prefix-";
    static const char codx[] = "/out/cod2x-key-legacy/prefix-";
    char prefix[96];
    DWORD length = GetEnvironmentVariableA("WINEPREFIX", prefix, sizeof(prefix));
    if (!GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "wine_get_version") ||
        !length || length >= sizeof(prefix) ||
        GetEnvironmentVariableA("COD2_UNBOUND_TEST_KEY_SLOT", NULL, 0))
        return 0;
    const char *suffix;
    if (!strncmp(prefix, original, sizeof(original) - 1))
        suffix = prefix + sizeof(original) - 1;
    else if (!strncmp(prefix, codx, sizeof(codx) - 1))
        suffix = prefix + sizeof(codx) - 1;
    else
        return 0;
    return strlen(suffix) == 8 && strspn(suffix, "abcdefghijklmnopqrstuvwxyz0123456789_") == 8;
}

static int LegacyValues(int seed)
{
    for (size_t i = 0; i < sizeof(legacy) / sizeof(legacy[0]); ++i) {
        HKEY key;
        LONG status;
        if (seed) {
            if (RegCreateKeyExA(legacy[i].root, legacy[i].path, 0, NULL, 0,
                                KEY_SET_VALUE | legacy[i].view, NULL, &key, NULL) != ERROR_SUCCESS)
                return 0;
            status = RegSetValueExA(key, "codkey", 0, REG_SZ, (const BYTE *)legacy[i].value, 21);
        } else {
            char value[21];
            DWORD size = sizeof(value);
            if (RegOpenKeyExA(legacy[i].root, legacy[i].path, 0,
                              KEY_QUERY_VALUE | legacy[i].view, &key) != ERROR_SUCCESS)
                return 0;
            status = RegGetValueA(key, NULL, "codkey", RRF_RT_REG_SZ, NULL, value, &size);
            if (status == ERROR_SUCCESS && (size != sizeof(value) || strcmp(value, legacy[i].value)))
                status = ERROR_INVALID_DATA;
            SecureZeroMemory(value, sizeof(value));
        }
        RegCloseKey(key);
        if (status != ERROR_SUCCESS)
            return 0;
    }
    return 1;
}

int main(int argc, char **argv)
{
    if (!PrivateWine()) {
        fputs("The legacy key probe requires the private path-check Wine prefix and no test slot.\n", stderr);
        return 2;
    }
    char result[21];
    if (argc == 2 && !strcmp(argv[1], "verify")) {
        if (!Win_ReadCDKey(result) || strcmp(result, savedKey) || !LegacyValues(0)) return 5;
        SecureZeroMemory(result, sizeof(result));
        puts("PASS engine startup preserved the saved key and both legacy values");
        return 0;
    }
    if (argc != 1) return 2;
    if (Win_ReadCDKey(result) || !LegacyValues(1) ||
        !Win_ReadCDKey(result) || strcmp(result, retailKey)) return 3;
    if (!Win_WriteCDKey(savedKey) || !Win_ReadCDKey(result) ||
        strcmp(result, savedKey) || !LegacyValues(0)) return 4;
    SecureZeroMemory(result, sizeof(result));
    puts("PASS retail fallback without a saved key and save preserved both legacy values");
    return 0;
}
