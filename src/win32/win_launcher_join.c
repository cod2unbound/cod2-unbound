#include "cod2_platform.h"
#if COD2_WINDOWS_NATIVE && COD2_X64 && !defined(DEDICATED)
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "PC/qcommon/join_link_parser.h"
#include "win_launcher_join.h"

extern void Cbuf_AddText(const char *text);
extern void Dvar_SetStringByName(const char *name, const char *value);

static HWND joinWindow;
static JoinLink pendingJoin;

static LRESULT CALLBACK Win_LauncherJoinMessage(HWND window, UINT message, WPARAM sender, LPARAM data)
{
    if (message != WM_COPYDATA)
        return DefWindowProcW(window, message, sender, data);

    const COPYDATASTRUCT *copy = (const COPYDATASTRUCT *)data;
    if (!copy || copy->dwData != 0x434F4432 || copy->cbData < 2 || copy->cbData > 2048 || !copy->lpData ||
        pendingJoin.address[0])
        return FALSE;

    const char *url = copy->lpData;
    if (memchr(url, 0, copy->cbData) != url + copy->cbData - 1)
        return FALSE;
    return JoinLink_ParsePayload(url, &pendingJoin) ? TRUE : FALSE;
}

void Win_LauncherJoinStartup(void)
{
    char payload[2048];
    DWORD length = GetEnvironmentVariableA("COD2_UNBOUND_JOIN", payload, sizeof(payload));
    if (length) {
        /* Clear the child environment before configuration or engine output. */
        _putenv("COD2_UNBOUND_JOIN=");
        SetEnvironmentVariableA("COD2_UNBOUND_JOIN", NULL);
        if (length < sizeof(payload))
            JoinLink_ParsePayload(payload, &pendingJoin);
        SecureZeroMemory(payload, sizeof(payload));
    }
}

void Win_LauncherJoinInit(void)
{
    const wchar_t className[] = L"CoD2Unbound.LauncherJoin.1";
    HINSTANCE instance = GetModuleHandleW(NULL);
    WNDCLASSW descriptor = {0};
    descriptor.lpfnWndProc = Win_LauncherJoinMessage;
    descriptor.hInstance = instance;
    descriptor.lpszClassName = className;
    if (!RegisterClassW(&descriptor))
        return;

    wchar_t name[16];
    swprintf(name, sizeof(name) / sizeof(name[0]), L"%lu", (unsigned long)GetCurrentProcessId());
    joinWindow = CreateWindowExW(0, className, name, 0, 0, 0, 0, 0, HWND_MESSAGE, NULL, instance, NULL);
}

void Win_LauncherJoinFrame(void)
{
    MSG message;
    /* PeekMessage services sent messages even when there are no posted messages. */
    while (joinWindow && PeekMessageW(&message, joinWindow, 0, 0, PM_REMOVE))
        DispatchMessageW(&message);
    if (!pendingJoin.address[0])
        return;

    /* Put only the validated connect in the command buffer. A password never
       becomes console text or part of the process command line. */
    char command[sizeof(pendingJoin.address) + 10];
    snprintf(command, sizeof(command), "connect %s\n", pendingJoin.address);
    Dvar_SetStringByName("password", pendingJoin.password);
    SecureZeroMemory(&pendingJoin, sizeof(pendingJoin));
    Cbuf_AddText(command);
}
#endif
