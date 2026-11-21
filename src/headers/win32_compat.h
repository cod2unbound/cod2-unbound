#ifndef WIN32_COMPAT_H
#define WIN32_COMPAT_H

#ifdef _WIN32

#include <io.h>
#include <direct.h>

#if COD2_WINDOWS_NATIVE
#include <dirent.h>
#include "win32/win_files.h"
#ifndef COD2_WIN_RAW_FILE_IO
#define fopen Win_FileOpen
#define access Win_Access
#define mkdir(path, mode) Win_Mkdir(path)
#define rmdir Win_Rmdir
#define remove Win_Remove
#define rename Win_Rename
#define stat(path, status) Win_Stat(path, status)
#define DIR Win_Directory
#define dirent Win_DirectoryEntry
#define opendir Win_OpenDirectory
#define readdir Win_ReadDirectory
#define closedir Win_CloseDirectory
#endif
#elif !defined(COD2_NO_MKDIR_SHIM)
#define mkdir(path, mode) _mkdir(path)
#endif

static __inline int pthread_main_np(void) { return 1; }

#endif
#endif
