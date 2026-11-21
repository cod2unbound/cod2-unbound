#ifndef COD2_WIN_PATHS_H
#define COD2_WIN_PATHS_H

#include <stddef.h>
#include <wchar.h>

/* Keep physical paths out of the engine's fixed-size archive and download records. */
#define WIN_DATA_ROOT "@data"
#define WIN_USER_ROOT "@user"
#define WIN_PATH_CAPACITY 32768

char *Win_CommandLine(void);
int Win_ConfigurePaths(char *arguments);
wchar_t *Win_OSPath(const char *path, int writing);
wchar_t *Win_ExtendedPath(const wchar_t *path);
char *Win_UTF8(const wchar_t *path);
wchar_t *Win_UTF16(const char *path);
const char *Win_ExecutablePath(void);
const char *Win_ExecutableDirectory(void);

#endif
