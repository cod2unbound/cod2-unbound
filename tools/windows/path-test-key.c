/* Wine fixture helper. The in-file values only exercise the CRC format. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincred.h>
#include <string.h>
#include <stdio.h>
#include "win32/win_key_slot.h"
#include "win32/win_paths.h"

int main(int argc, char **argv)
{
    static const char sample[] = "000000000000000086D3";
    static const char invalid[] = "00000000000000000000";
    wchar_t target[WIN_TEST_KEY_TARGET_SIZE];
    char result[21];
    if (!Win_TestKeyTarget(target)) return 2;

    if (argc == 2 && !strcmp(argv[1], "verify")) {
        if (!Win_ReadCDKey(result) || strcmp(result, sample)) return 5;
        SecureZeroMemory(result, sizeof(result));
        if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0) || Win_ReadCDKey(result)) return 6;
        puts("PASS engine preserved the test key and deleting the slot left no key");
        return 0;
    }
    if (argc != 1) return 2;
    if (Win_ReadCDKey(result) || !Win_WriteCDKey(sample) ||
        !Win_ReadCDKey(result) || strcmp(result, sample)) return 1;

    CREDENTIALW entry = {0};
    entry.Type = CRED_TYPE_GENERIC;
    entry.TargetName = target;
    entry.CredentialBlob = (BYTE *)invalid;
    entry.CredentialBlobSize = 20;
    entry.Persist = CRED_PERSIST_LOCAL_MACHINE;
    if (!CredWriteW(&entry, 0) || Win_ReadCDKey(result)) return 3;
    entry.CredentialBlob = (BYTE *)sample;
    if (!CredWriteW(&entry, 0) || !Win_ReadCDKey(result) || strcmp(result, sample)) return 4;
    SecureZeroMemory(result, sizeof(result));
    puts("PASS empty slot, in-game save, invalid explicit credential and launcher precedence");
    return 0;
}
