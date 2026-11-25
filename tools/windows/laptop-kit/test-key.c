/* Synthetic credential probe. Every action requires a valid test slot. */
#include "cod2_platform.h"
#if COD2_WINDOWS_NATIVE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincred.h>
#include <string.h>
#include "win32/win_key_slot.h"

static const char sample[] = "000000000000000086D3";

int main(int argc, char **argv)
{
    wchar_t target[WIN_TEST_KEY_TARGET_SIZE];
    if (argc != 2 || !Win_TestKeyTarget(target)) return 2;

    PCREDENTIALW entry;
    int present = CredReadW(target, CRED_TYPE_GENERIC, 0, &entry);
    int matches = 0;
    if (present) {
        matches = entry->CredentialBlobSize == 20 && !memcmp(entry->CredentialBlob, sample, 20);
        SecureZeroMemory(entry->CredentialBlob, entry->CredentialBlobSize);
        CredFree(entry);
    } else if (GetLastError() != ERROR_NOT_FOUND) {
        return 1;
    }

    if (!strcmp(argv[1], "absent")) return present ? 1 : 0;
    if (!strcmp(argv[1], "verify")) return matches ? 0 : 1;
    if (!strcmp(argv[1], "clear"))
        return !present || CredDeleteW(target, CRED_TYPE_GENERIC, 0) ? 0 : 1;
    if (strcmp(argv[1], "seed")) return 2;
    if (present) return 1;

    CREDENTIALW fixture = {0};
    fixture.Type = CRED_TYPE_GENERIC;
    fixture.TargetName = target;
    fixture.CredentialBlob = (BYTE *)sample;
    fixture.CredentialBlobSize = 20;
    fixture.Persist = CRED_PERSIST_LOCAL_MACHINE;
    return CredWriteW(&fixture, 0) ? 0 : 1;
}
#endif
