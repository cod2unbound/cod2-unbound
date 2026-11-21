/* Synthetic credential probe for a disposable Windows account. */
#include "cod2_platform.h"
#if COD2_WINDOWS_NATIVE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wincred.h>
#include <stdio.h>
#include <string.h>
#include "win32/win_paths.h"

static wchar_t target[] = L"CoD2 Unbound/CD Key";
static const char sample[] = "000000000000000086D3";
static const char invalid[] = "00000000000000000000";

static int WriteFixture(const char *value)
{
    CREDENTIALW entry = {0};
    entry.Type = CRED_TYPE_GENERIC;
    entry.TargetName = target;
    entry.CredentialBlob = (BYTE *)value;
    entry.CredentialBlobSize = 20;
    entry.Persist = CRED_PERSIST_LOCAL_MACHINE;
    return CredWriteW(&entry, 0);
}

/* Do not call the engine's fallback lookup when the explicit entry is missing. */
static int HasFixture(void)
{
    PCREDENTIALW entry;
    if (!CredReadW(target, CRED_TYPE_GENERIC, 0, &entry))
        return 0;
    int matches = entry->CredentialBlobSize == 20 && !memcmp(entry->CredentialBlob, sample, 20);
    SecureZeroMemory(entry->CredentialBlob, entry->CredentialBlobSize);
    CredFree(entry);
    return matches;
}

int main(int argc, char **argv)
{
    char key[21];
    int ok;

    if (argc != 2)
        return 2;

    if (!strcmp(argv[1], "seed")) {
        /* The caller has checked the target is absent with cmdkey. */
        return Win_WriteCDKey(sample) ? 0 : 1;
    }
    if (!HasFixture())
        return 1;
    if (!strcmp(argv[1], "clear"))
        return CredDeleteW(target, CRED_TYPE_GENERIC, 0) ? 0 : 1;
    if (!strcmp(argv[1], "verify"))
        return 0;
    if (strcmp(argv[1], "check"))
        return 2;

    ok = Win_ReadCDKey(key) && !strcmp(key, sample);
    SecureZeroMemory(key, sizeof(key));
    if (!ok || !WriteFixture(invalid))
        return 1;
    ok = !Win_ReadCDKey(key);
    SecureZeroMemory(key, sizeof(key));
    return WriteFixture(sample) && ok ? 0 : 1;
}
#endif
