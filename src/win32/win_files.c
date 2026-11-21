#include "cod2_platform.h"
#if COD2_WINDOWS_NATIVE
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <errno.h>
#include <fcntl.h>
#include <io.h>
#include <stdlib.h>
#include "win_paths.h"
#include "win_files.h"

struct Win_Directory {
    HANDLE find;
    WIN32_FIND_DATAW data;
    struct Win_DirectoryEntry entry;
    int first;
};

static void FileError(void)
{
    DWORD error = GetLastError();
    errno = (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) ? ENOENT :
        (error == ERROR_ALREADY_EXISTS || error == ERROR_FILE_EXISTS) ? EEXIST : EACCES;
}

Win_Directory *Win_OpenDirectory(const char *path)
{
    wchar_t *folder = Win_OSPath(path, 0);
    if (!folder) return NULL;
    size_t length = wcslen(folder);
    wchar_t *pattern = realloc(folder, (length + 3) * sizeof(*pattern));
    if (!pattern) { free(folder); return NULL; }
    wcscpy(pattern + length, length && pattern[length - 1] == L'\\' ? L"*" : L"\\*");
    Win_Directory *directory = malloc(sizeof(*directory));
    if (directory) {
        directory->find = FindFirstFileW(pattern, &directory->data);
        directory->first = 1;
        if (directory->find == INVALID_HANDLE_VALUE) {
            FileError();
            free(directory);
            directory = NULL;
        }
    }
    free(pattern);
    return directory;
}

struct Win_DirectoryEntry *Win_ReadDirectory(Win_Directory *directory)
{
    for (;;) {
        if (directory->first) directory->first = 0;
        else if (!FindNextFileW(directory->find, &directory->data)) {
            if (GetLastError() != ERROR_NO_MORE_FILES) FileError();
            return NULL;
        }
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, directory->data.cFileName,
                                -1, directory->entry.d_name, sizeof(directory->entry.d_name), NULL, NULL))
            return &directory->entry;
    }
}

int Win_CloseDirectory(Win_Directory *directory)
{
    int ok = FindClose(directory->find);
    free(directory);
    return ok ? 0 : -1;
}

/* Reject writes through existing hard links as well as reparse points. */
static int SafeFile(const wchar_t *path)
{
    BY_HANDLE_FILE_INFORMATION info;
    HANDLE file = CreateFileW(path, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
    }
    int ok = GetFileInformationByHandle(file, &info) && info.nNumberOfLinks == 1 &&
        !(info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY));
    CloseHandle(file);
    return ok;
}

FILE *Win_FileOpen(const char *path, const char *mode)
{
    int writing = mode[0] != 'r' || strchr(mode, '+') != NULL;
    wchar_t *filename = Win_OSPath(path, writing);
    wchar_t *wideMode = Win_UTF16(mode);
    FILE *file = NULL;
    if (filename && wideMode) {
        if (!writing || SafeFile(filename)) file = _wfopen(filename, wideMode);
        else errno = EACCES;
    }
    free(filename);
    free(wideMode);
    return file;
}

int Win_OpenWrite(const char *path)
{
    wchar_t *filename = Win_OSPath(path, 1);
    int file = -1;
    if (filename) {
        if (SafeFile(filename)) file = _wopen(filename, _O_CREAT | _O_TRUNC | _O_WRONLY | _O_BINARY,
                                              _S_IREAD | _S_IWRITE);
        else errno = EACCES;
        free(filename);
    }
    return file;
}

int Win_Stat(const char *path, struct stat *status)
{
    wchar_t *filename = Win_OSPath(path, 0);
    WIN32_FILE_ATTRIBUTE_DATA data;
    if (!filename) return -1;
    int ok = GetFileAttributesExW(filename, GetFileExInfoStandard, &data);
    if (!ok) FileError();
    memset(status, 0, sizeof(*status));
    if (ok && (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
        BY_HANDLE_FILE_INFORMATION info;
        HANDLE folder = CreateFileW(filename, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, NULL);
        if (folder != INVALID_HANDLE_VALUE) {
            if (GetFileInformationByHandle(folder, &info)) {
                status->st_dev = info.dwVolumeSerialNumber;
                status->st_ino = info.nFileIndexLow ^ info.nFileIndexHigh;
            }
            CloseHandle(folder);
        }
    }
    free(filename);
    if (!ok) return -1;
    status->st_mode = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ? _S_IFDIR : _S_IFREG;
    status->st_mode |= _S_IREAD;
    if (!(data.dwFileAttributes & FILE_ATTRIBUTE_READONLY)) status->st_mode |= _S_IWRITE;
    status->st_size = ((uint64_t)data.nFileSizeHigh << 32) | data.nFileSizeLow;
    ULARGE_INTEGER time;
    time.LowPart = data.ftLastWriteTime.dwLowDateTime;
    time.HighPart = data.ftLastWriteTime.dwHighDateTime;
    if (time.QuadPart >= 116444736000000000ULL)
        status->st_mtime = (time.QuadPart - 116444736000000000ULL) / 10000000;
    return 0;
}

int Win_Access(const char *path, int mode)
{
    struct stat status;
    if (Win_Stat(path, &status)) return -1;
    if (!(mode & 2) || (status.st_mode & _S_IWRITE)) return 0;
    errno = EACCES;
    return -1;
}

int Win_Mkdir(const char *path)
{
    wchar_t *directory = Win_OSPath(path, 1);
    if (!directory) return -1;
    int ok = CreateDirectoryW(directory, NULL);
    if (!ok) FileError();
    free(directory);
    return ok ? 0 : -1;
}

int Win_Rmdir(const char *path)
{
    wchar_t *directory = Win_OSPath(path, 1);
    if (!directory) return -1;
    int ok = RemoveDirectoryW(directory);
    if (!ok) FileError();
    free(directory);
    return ok ? 0 : -1;
}

int Win_Remove(const char *path)
{
    wchar_t *filename = Win_OSPath(path, 1);
    if (!filename) return -1;
    int ok = DeleteFileW(filename);
    if (!ok) FileError();
    free(filename);
    return ok ? 0 : -1;
}

int Win_Move(const char *from, const char *to, unsigned long flags)
{
    wchar_t *source = Win_OSPath(from, 1), *target = Win_OSPath(to, 1);
    int ok = source && target && MoveFileExW(source, target, flags);
    if (source && target && !ok) FileError();
    free(source);
    free(target);
    return ok;
}

int Win_Rename(const char *from, const char *to)
{
    return Win_Move(from, to, MOVEFILE_REPLACE_EXISTING) ? 0 : -1;
}

int Win_MakeWritable(const char *path)
{
    wchar_t *filename = Win_OSPath(path, 1);
    if (!filename) return 0;
    DWORD attributes = GetFileAttributesW(filename);
    int ok = attributes != INVALID_FILE_ATTRIBUTES && SafeFile(filename) &&
        (!(attributes & FILE_ATTRIBUTE_READONLY) || SetFileAttributesW(filename, attributes & ~FILE_ATTRIBUTE_READONLY));
    free(filename);
    return ok;
}
#endif
