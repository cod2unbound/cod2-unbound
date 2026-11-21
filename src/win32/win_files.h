#ifndef COD2_WIN_FILES_H
#define COD2_WIN_FILES_H

#include <stdio.h>
#include <sys/stat.h>
#include <stdint.h>

/* Legacy engine paths are UTF-8. Their physical roots are expanded only here. */
typedef struct Win_Directory Win_Directory;
struct Win_DirectoryEntry { char d_name[4 * 256]; };
Win_Directory *Win_OpenDirectory(const char *path);
struct Win_DirectoryEntry *Win_ReadDirectory(Win_Directory *directory);
int Win_CloseDirectory(Win_Directory *directory);

FILE *Win_FileOpen(const char *path, const char *mode);
int Win_OpenWrite(const char *path);
int Win_Stat(const char *path, struct stat *status);
int Win_Access(const char *path, int mode);
int Win_Mkdir(const char *path);
int Win_Rmdir(const char *path);
int Win_Remove(const char *path);
int Win_Move(const char *from, const char *to, unsigned long flags);
int Win_Rename(const char *from, const char *to);
int Win_MakeWritable(const char *path);
#endif
