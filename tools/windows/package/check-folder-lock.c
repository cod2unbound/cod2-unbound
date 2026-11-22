/* Hold the install directory open without FILE_SHARE_DELETE until released. */
#include <windows.h>

int wmain(int argc, wchar_t **argv)
{
    if (argc != 4)
        return 2;
    HANDLE directory = CreateFileW(argv[1], GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   NULL, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
    if (directory == INVALID_HANDLE_VALUE)
        return 3;
    HANDLE ready = CreateFileW(argv[2], GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_NEW,
                               FILE_ATTRIBUTE_NORMAL, NULL);
    if (ready == INVALID_HANDLE_VALUE) {
        CloseHandle(directory);
        return 4;
    }
    CloseHandle(ready);
    while (GetFileAttributesW(argv[3]) == INVALID_FILE_ATTRIBUTES)
        Sleep(25);
    CloseHandle(directory);
    return 0;
}
