#ifndef WIN_KEY_SLOT_H
#define WIN_KEY_SLOT_H

#define WIN_TEST_KEY_SLOT L"COD2_UNBOUND_TEST_KEY_SLOT"
#define WIN_TEST_KEY_TARGET_SIZE 64

/* This helper can only construct test targets. Invalid and absent slots both fail. */
static inline int Win_TestKeyTarget(wchar_t target[WIN_TEST_KEY_TARGET_SIZE])
{
    wchar_t slot[33];
    DWORD length = GetEnvironmentVariableW(WIN_TEST_KEY_SLOT, slot, ARRAYSIZE(slot));
    if (!length || length >= ARRAYSIZE(slot)) return 0;
    for (DWORD i = 0; i < length; ++i) {
        wchar_t ch = slot[i];
        if (!(ch >= L'a' && ch <= L'z') && !(ch >= L'0' && ch <= L'9') && ch != L'-')
            return 0;
    }
    wcscpy(target, L"CoD2 Unbound/CD Key (test ");
    wcscat(target, slot);
    wcscat(target, L")");
    return 1;
}

#endif
