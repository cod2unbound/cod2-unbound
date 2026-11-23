#include "common_types.h"
#include "imports.h"
#include <stdio.h>
#if COD2_WINDOWS_NATIVE
#include "win32/win_paths.h"
#endif

extern FILE *FS_FileOpen(const char *filename, const char *mode);
extern size_t FS_FileRead(void *buffer, size_t size, size_t count, FILE *stream);
extern int FS_FileClose(FILE *stream);
extern int FS_FileSeek(FILE *file, long int offset, int origin);
extern qboolean SEH_GetLanguageIndexForName(const char *name, language_t *lang);
extern void Com_BeginParseSession(const char *name);
extern void Com_EndParseSession(void);
extern const char *Com_Parse(const char **buf);
extern int strcmp(const char *s1, const char *s2);
extern char *va(const char *fmt, ...);

static char *localization;
static char *localized_strings;
static char language_buffer[4096];

#if COD2_WINDOWS_NATIVE
/* The English startup strings from the 1.3 localization.txt. The game may
 * have only the required IWDs, and these dialogs can precede IWD startup. */
static const struct {
    const char *ref;
    const char *text;
} startupEnglish[] = {
    { "WIN_CONFIGURE_UPDATED_TITLE", "Recommended Settings Updated" },
    { "WIN_CONFIGURE_UPDATED_BODY",
        "The recommended settings have been updated since the last time you ran Call of Duty(R) 2.\n"
        "Would you like the game to configure itself optimally with these new settings?\n\n"
        "This is recommended for most people.  It will change your system settings but not your controls." },
    { "WIN_COMPUTER_CHANGE_TITLE", "Set Optimal Settings?" },
    { "WIN_COMPUTER_CHANGE_BODY",
        "Your computer appears to have changed since the last time you ran Call of Duty(R) 2.\n"
        "Would you like the game to configure itself optimally for your new hardware?\n\n"
        "This is recommended for most people.\n"
        "It will change your system settings but not your controls." },
    { "WIN_DIRECTX_INIT_TITLE", "DirectX Error" },
    { "WIN_DIRECTX_INIT_BODY",
        "DirectX(R) encountered an unrecoverable error.\n\n"
        "Check the DirectX(R) FAQ for possible solutions." },
    { "WIN_OUT_OF_MEM_TITLE", "Out of memory error" },
    { "WIN_OUT_OF_MEM_BODY", "Out of memory error.  You are probably low on hard disk space." },
    { "WIN_IMPROPER_QUIT_TITLE", "Run In Safe Mode?" },
    { "WIN_IMPROPER_QUIT_BODY",
        "It appears that Call of Duty(R) 2 did not quit properly the last time it ran.\n"
        "Do you want to run the game in safe mode?\n\n"
        "This is recommended for most people.\n"
        "It will change your system settings but not your controls." },
    { "WIN_ERROR", "Fatal Error" },
    { "WIN_LOW_MEMORY_TITLE", "Low Memory" },
    { "WIN_LOW_MEMORY_BODY",
        "It looks like you are low on virtual memory.\n"
        "This can cause the game to run slowly and it may stop completely.\n"
        "It is highly recommended that you close some programs before running Call of Duty(R) 2.\n\n"
        "Do you want to run Call of Duty(R) 2 anyway?" },
    { "WIN_COLORDEPTH_WARN_TITLE", "Low Desktop Color Depth" },
    { "WIN_COLORDEPTH_WARN_BODY",
        "It is highly unlikely that a correct windowed display can be\n"
        "initialized with the current desktop display depth.  Select\n"
        "'OK' to try anyway.  Press 'Cancel' if you wish to quit." },
    { "WIN_DISK_FULL_TITLE", "File Write Error" },
    { "WIN_DISK_FULL_BODY", "Call of Duty(R) 2 couldn't write a file.  The hard drive is probably full." },
    { "WIN_UNABLE_LOAD_DLL_BODY",
        "Unable to load a DLL.  Possibly due to low memory, insufficient hard drive space, or missing DLL files." },
};

static const char *Win_StartupEnglish(const char *ref)
{
    for (size_t i = 0; i < sizeof(startupEnglish) / sizeof(startupEnglish[0]); ++i) {
        if (!strcmp(startupEnglish[i].ref, ref))
            return startupEnglish[i].text;
    }
    return "Call of Duty 2";
}
#endif

language_t Win_InitLocalization(void)
{
    FILE *fp;
    int size;
    char *p;
    language_t lang;

    localization = 0;
    localized_strings = 0;

#if COD2_WINDOWS_NATIVE
    fp = FS_FileOpen(WIN_DATA_ROOT "/localization.txt", "r");
#else
    fp = FS_FileOpen("localization.txt", "r");
#endif
    if (!fp)
        return (language_t)(0);

    FS_FileSeek(fp, 0, 2);
    size = (int)ftell(fp);
    FS_FileSeek(fp, 0, 0);
#if COD2_WINDOWS_NATIVE
    /* The Windows data directory can be selected by the user. */
    if (size < 0 || size >= (int)sizeof(language_buffer)) {
        FS_FileClose(fp);
        return (language_t)(0);
    }
#endif

    localization = language_buffer;
    size = (int)FS_FileRead(language_buffer, 1, (size_t)size, fp);
    FS_FileClose(fp);

    if (!size) {
        localization = 0;
        return (language_t)(0);
    }
    localization[size] = '\0';

    lang = (language_t)(0);
    p = localization;
    while (*p) {
        if (*p == '\n') {
            *p = '\0';
            p++;
            localized_strings = p;
            SEH_GetLanguageIndexForName(localization, &lang);
            break;
        }
        p++;
    }

    return lang;
}

void Win_ShutdownLocalization(void)
{
    localization = 0;
    localized_strings = 0;
}

const char *Win_LocalizeRef(const char *ref)
{
    const char *strings;
    const char *key;
    const char *value;
    int match;

#if COD2_WINDOWS_NATIVE
    if (!localized_strings)
        return Win_StartupEnglish(ref);
#endif
    Com_BeginParseSession("localization");
    strings = localized_strings;

    for (;;) {
        key = Com_Parse(&strings);
        if (*key == '\0')
            break;

        match = strcmp(key, ref);

        value = Com_Parse(&strings);
        if (*value == '\0')
            break;

        if (match == 0) {
            Com_EndParseSession();
            return va("%s", value);
        }
    }

    Com_EndParseSession();
#if COD2_WINDOWS_NATIVE
    return Win_StartupEnglish(ref);
#else
    return va("%s", ref);
#endif
}
