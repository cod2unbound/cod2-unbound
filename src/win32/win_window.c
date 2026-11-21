/* The game window's handle, for the Windows APIs that bind to one. */
#include <SDL2/SDL.h>
#include <SDL2/SDL_syswm.h>
#include <windows.h>

extern SDL_Window *sdl_gl_window;

void *Win_GameWindow(void)
{
    SDL_SysWMinfo info;

    SDL_VERSION(&info.version);
    if (sdl_gl_window && SDL_GetWindowWMInfo(sdl_gl_window, &info))
        return info.info.win.window;
    return GetDesktopWindow();
}

#if COD2_WINDOWS_NATIVE && COD2_X64
#include "win_window.h"

/* Windows 1.3 MapKey (0x468d40). Reference: nawaftahir's reconstruction of
 * win_wndproc.cpp. The scan code disambiguates console and extended keypad
 * keys. The layout's virtual key supplies the bind name, not the typed text. */
static int Win_MapKey(WPARAM vk, LPARAM flags)
{
    static const unsigned char navigation[] = {184, 190, 188, 182, 185, 183, 187, 189};
    static const unsigned char extendedNav[] = {164, 163, 166, 165, 156, 154, 157, 155};
    static const unsigned char digits[] = {192, 188, 189, 190, 185, 186, 187, 182, 183, 184};
    static const unsigned char labels[] = {
        181, 191, 223, 224, 225, 228, 229, 230, 231, 232,
        233, 236, 241, 242, 243, 246, 248, 249, 250, 252
    };
    const int extended = (flags >> 24) & 1;

    if (((flags >> 16) & 0xff) == 0x29)
        return '~';
    if (vk >= 'A' && vk <= 'Z')
        return (int)vk + (extended ? 0 : 'a' - 'A');
    if (vk >= '0' && vk <= '9')
        return (int)vk;
    if (vk >= VK_F1 && vk <= VK_F12)
        return 167 + (int)(vk - VK_F1);
    if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9)
        return digits[vk - VK_NUMPAD0];
    if (vk >= VK_PRIOR && vk <= VK_DOWN)
        return (extended ? extendedNav : navigation)[vk - VK_PRIOR];
    switch (vk) {
    case VK_LBUTTON:  return 200;
    case VK_RBUTTON:  return 201;
    case VK_MBUTTON:  return 202;
    case VK_XBUTTON1: return 203;
    case VK_XBUTTON2: return 204;
    case VK_BACK:     return 127;
    case VK_TAB:      return 9;
    case VK_CLEAR:    if (!extended) return 186; break;
    case VK_RETURN:   return extended ? 191 : 13;
    case VK_SHIFT:    return 160;
    case VK_CONTROL:  return 159;
    case VK_MENU:     return 158;
    case VK_PAUSE:    return 153;
    case VK_CAPITAL:  return 151;
    case VK_ESCAPE:   return 27;
    case VK_SPACE:    return 32;
    case VK_INSERT:   return extended ? 161 : 192;
    case VK_DELETE:   return extended ? 162 : 193;
    case VK_MULTIPLY: return 198;
    case VK_ADD:      return 196;
    case VK_SUBTRACT: return 195;
    case VK_DECIMAL:  return 193;
    case VK_DIVIDE:   return 194;
    case VK_NUMLOCK:  return 197;
    }
    unsigned result = MapVirtualKeyA((UINT)vk, MAPVK_VK_TO_CHAR) & 0xff;
    for (unsigned i = 0; i < sizeof(labels); ++i)
        if (result == labels[i])
            return 128 + i;
    return result;
}

static UINT legacyWheelMessage;

static int SDLCALL Win_FilterInputMessages(void *context, SDL_Event *event)
{
    (void)context;
    if (event->type != SDL_SYSWMEVENT)
        return 1;
    const SDL_SysWMmsg *message = event->syswm.msg;
    if (message->subsystem != SDL_SYSWM_WINDOWS)
        return 0;
    switch (message->msg.win.msg) {
    case WM_KEYDOWN:
    case WM_KEYUP:
    case WM_SYSKEYDOWN:
    case WM_SYSKEYUP:
    case WM_MOUSEWHEEL:
        return 1;
    case WM_CHAR:
        /* SDL sends the printable text and IME composition separately. */
        return message->msg.win.wParam < 32 && message->msg.win.wParam != 0;
    default:
        return legacyWheelMessage && message->msg.win.msg == legacyWheelMessage;
    }
}

void Win_InputEnableMessages(void)
{
    legacyWheelMessage = RegisterWindowMessageA("MSWHEEL_ROLLMSG");
    SDL_SetEventFilter(Win_FilterInputMessages, NULL);
    SDL_EventState(SDL_SYSWMEVENT, SDL_ENABLE);
    SDL_EventState(SDL_KEYDOWN, SDL_DISABLE);
    SDL_EventState(SDL_KEYUP, SDL_DISABLE);
    SDL_EventState(SDL_MOUSEWHEEL, SDL_DISABLE);
    SDL_SetHint(SDL_HINT_MOUSE_AUTO_CAPTURE, "0");
}

enum WinInputKind Win_InputMessage(const SDL_SysWMmsg *message, int *value, int *down)
{
    if (message->msg.win.hwnd != Win_GameWindow())
        return WIN_INPUT_NONE;
    UINT id = message->msg.win.msg;
    WPARAM vk = message->msg.win.wParam;
    switch (id) {
    case WM_SYSKEYDOWN:
        if (vk == VK_RETURN)
            return WIN_INPUT_ALT_ENTER;
        /* fall through */
    case WM_KEYDOWN:
    case WM_KEYUP:
    case WM_SYSKEYUP:
        *value = Win_MapKey(vk, message->msg.win.lParam);
        *down = id == WM_KEYDOWN || id == WM_SYSKEYDOWN;
        return *value ? WIN_INPUT_KEY : WIN_INPUT_NONE;
    case WM_CHAR:
        *value = (int)vk;
        return WIN_INPUT_CONTROL;
    case WM_MOUSEWHEEL:
        *value = (short)HIWORD(vk) > 0 ? 1 : -1;
        return WIN_INPUT_WHEEL;
    default:
        *value = (int)vk > 0 ? 1 : -1;
        return WIN_INPUT_WHEEL; /* The filter only admits the old wheel message here. */
    }
}

char *Win_InputTextToAnsi(const char *utf8)
{
    int wideLength = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8, -1, NULL, 0);
    if (!wideLength)
        return NULL;
    WCHAR *wide = malloc((size_t)wideLength * sizeof(*wide));
    if (!wide)
        return NULL;
    char *bytes = NULL;
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8, -1, wide, wideLength)) {
        int byteLength = WideCharToMultiByte(CP_ACP, 0, wide, -1, NULL, 0, NULL, NULL);
        if (byteLength) {
            bytes = malloc((size_t)byteLength);
            if (bytes && !WideCharToMultiByte(CP_ACP, 0, wide, -1, bytes, byteLength, NULL, NULL)) {
                free(bytes);
                bytes = NULL;
            }
        }
    }
    free(wide);
    return bytes;
}

/* Compare retail IN_ActivateWin32Mouse and IN_Win32Mouse. The center uses the
 * outer window rectangle. Reading the OS cursor keeps pointer speed and
 * Enhance pointer precision in the input path. */
static HWND capturedWindow;
static POINT cursorCenter;

static int Win_InputCursorCenter(HWND window, POINT *center)
{
    RECT rect;
    if (!GetWindowRect(window, &rect))
        return 0;
    center->x = (rect.left + rect.right) / 2;
    center->y = (rect.top + rect.bottom) / 2;
    return 1;
}

int Win_InputCaptureCursor(int capture)
{
    HWND window = sdl_gl_window ? Win_GameWindow() : NULL;
    if (!capture || !window || GetForegroundWindow() != window) {
        if (capturedWindow && GetCapture() == capturedWindow)
            ReleaseCapture();
        capturedWindow = NULL;
        return 0;
    }
    if (capturedWindow != window || GetCapture() != window) {
        if (!Win_InputCursorCenter(window, &cursorCenter))
            return 0;
        SetCapture(window);
        if (!SetCursorPos(cursorCenter.x, cursorCenter.y)) {
            if (GetCapture() == window)
                ReleaseCapture();
            capturedWindow = NULL;
            return 0;
        }
        capturedWindow = window;
    }
    return 1;
}

int Win_InputCursorDelta(int *dx, int *dy)
{
    POINT position, center;
    if (!capturedWindow || GetForegroundWindow() != capturedWindow ||
        !Win_InputCursorCenter(capturedWindow, &center) || !GetCursorPos(&position) ||
        !SetCursorPos(center.x, center.y))
        return 0;
    /* A window move changes the center without moving the mouse. */
    *dx = center.x == cursorCenter.x && center.y == cursorCenter.y ? position.x - center.x : 0;
    *dy = center.x == cursorCenter.x && center.y == cursorCenter.y ? position.y - center.y : 0;
    cursorCenter = center;
    return *dx != 0 || *dy != 0;
}
#endif
