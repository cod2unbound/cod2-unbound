/* The Windows client's system Direct3D 9 path (COD2_WIN32_D3D9).
 *
 * The renderer calls the device through its COM vtables, as the original Windows
 * 1.3 renderer does. This file holds the parts that touch real Direct3D types:
 * loading d3d9.dll, the SDL window that owns the device, the presentation
 * parameters, shader compilation and constant tables, and screenshot readback.
 * It includes the MinGW d3d9.h and none of the engine headers, so the structure
 * layouts here are the system ones on either architecture. */
#define COBJMACROS
#include <windows.h>
#include <d3d9.h>
#include <d3dcompiler.h>
#include <SDL2/SDL.h>
#include <SDL2/SDL_syswm.h>
#if COD2_WINDOWS_NATIVE && COD2_CODX
#include <math.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "platform/macos_display.h"
#if COD2_WINDOWS_NATIVE
#include "win_frame_trace.h"
#endif

extern SDL_Window *sdl_gl_window;
extern int MacDisplay_Initialize(void);
#if COD2_WINDOWS_NATIVE
extern int Dvar_GetInt(const char *name);
#endif

#if COD2_WINDOWS_NATIVE && COD2_CODX
static HMONITOR gammaMonitor;
static HDC gammaDC;
static WORD originalGamma[3][256];
static float appliedGamma;
static int gammaChanged;

/* CoD2x v1.4.6.8 window.cpp gamma_update/gamma_restore. Windowed gamma
 * belongs to the current monitor. Exclusive gamma stays with the D3D device. */
void Win_D3D9_WindowGamma(int focused, float gamma)
{
    HMONITOR monitor = NULL;
    if (focused && sdl_gl_window && !MacDisplay_IsFullscreen() && gamma > 0 && gamma != 1.0f) {
        SDL_SysWMinfo info;
        SDL_VERSION(&info.version);
        if (SDL_GetWindowWMInfo(sdl_gl_window, &info))
            monitor = MonitorFromWindow(info.info.win.window, MONITOR_DEFAULTTONEAREST);
    }
    if (monitor != gammaMonitor) {
        if (gammaDC) {
            if (gammaChanged)
                SetDeviceGammaRamp(gammaDC, originalGamma);
            DeleteDC(gammaDC);
        }
        gammaDC = NULL;
        gammaChanged = 0;
        gammaMonitor = monitor;
        appliedGamma = 1.0f;
        if (monitor) {
            MONITORINFOEXA info = { .cbSize = sizeof(info) };
            if (GetMonitorInfoA(monitor, (MONITORINFO *)&info)) {
                gammaDC = CreateDCA(NULL, info.szDevice, NULL, NULL);
                if (gammaDC && !GetDeviceGammaRamp(gammaDC, originalGamma)) {
                    DeleteDC(gammaDC);
                    gammaDC = NULL;
                }
            }
        }
    }
    if (gammaDC && gamma != appliedGamma) {
        WORD ramp[3][256];
        for (int i = 0; i < 256; ++i)
            ramp[0][i] = ramp[1][i] = ramp[2][i] = (WORD)(pow(i / 255.0, 1.0 / gamma) * 65535.0 + 0.5);
        if (SetDeviceGammaRamp(gammaDC, ramp))
            gammaChanged = 1;
        else
            fputs("Couldn't apply windowed gamma on the current monitor\n", stderr);
        appliedGamma = gamma;
    }
}
#endif

typedef IDirect3D9 *(WINAPI *Direct3DCreate9Fn)(UINT sdkVersion);
typedef HRESULT(WINAPI *D3DCompileFn)(LPCVOID data, SIZE_T size, LPCSTR name, const D3D_SHADER_MACRO *defines,
                                      ID3DInclude *include, LPCSTR entry, LPCSTR target, UINT flags1,
                                      UINT flags2, ID3DBlob **code, ID3DBlob **messages);

void *Win_D3D9_Direct3DCreate9(void)
{
#if COD2_WINDOWS_NATIVE
    Win_FrameTraceInit();
#endif
    static Direct3DCreate9Fn create;
    if (!create) {
        HMODULE module = LoadLibraryA("d3d9.dll");
        if (module)
            create = (Direct3DCreate9Fn)(void *)GetProcAddress(module, "Direct3DCreate9");
        if (!create) {
            fprintf(stderr, "d3d9.dll or its Direct3DCreate9 is missing\n");
            return NULL;
        }
    }
    return create(D3D_SDK_VERSION);
}

#if COD2_WINDOWS_NATIVE
unsigned Win_D3D9_SelectAdapter(void *d3dInterface, int monitorNumber)
{
    IDirect3D9 *d3d = d3dInterface;
    HMONITOR monitor = NULL;
    if (MacDisplay_Initialize() != 0)
        return D3DADAPTER_DEFAULT;
    if (monitorNumber > 0) {
        SDL_Rect bounds;
        if (SDL_GetDisplayBounds(monitorNumber - 1, &bounds) == 0) {
            POINT center = {bounds.x + bounds.w / 2, bounds.y + bounds.h / 2};
            monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONULL);
        }
    } else {
        RECT saved = {Dvar_GetInt("vid_xpos"), Dvar_GetInt("vid_ypos"), 0, 0};
        saved.right = saved.left + 1;
        saved.bottom = saved.top + 1;
        monitor = MonitorFromRect(&saved, MONITOR_DEFAULTTONULL);
    }
    if (monitor) {
        UINT count = IDirect3D9_GetAdapterCount(d3d);
        for (UINT adapter = 0; adapter < count; ++adapter)
            if (IDirect3D9_GetAdapterMonitor(d3d, adapter) == monitor)
                return adapter;
    }
    return D3DADAPTER_DEFAULT;
}

/* The window the device presents to. It keeps SDL's window (and with it SDL's
 * input) and has no OpenGL context. Exclusive fullscreen belongs to the device:
 * the window only loses its border, and SDL never switches modes itself. */
void *Win_D3D9_PrepareWindow(void *d3dInterface, unsigned adapter, int width, int height,
                            int fullscreen, int refresh, int borderless)
{
    SDL_SysWMinfo info;
    MONITORINFO monitor = { .cbSize = sizeof(monitor) };
    int x = SDL_WINDOWPOS_CENTERED, y = SDL_WINDOWPOS_CENTERED;
    int mode = fullscreen ? MAC_FULLSCREEN : borderless ? MAC_BORDERLESS : MAC_WINDOWED;
    int windowWidth = width, windowHeight = height;
    int haveMonitor = GetMonitorInfoA(IDirect3D9_GetAdapterMonitor((IDirect3D9 *)d3dInterface, adapter), &monitor);

    if (MacDisplay_Initialize() != 0)
        return NULL;
#if COD2_CODX
    Win_D3D9_WindowGamma(0, 1.0f);
    if (!fullscreen && haveMonitor && width == monitor.rcMonitor.right - monitor.rcMonitor.left &&
        height == monitor.rcMonitor.bottom - monitor.rcMonitor.top)
        mode = MAC_BORDERLESS;
#endif
    MacPlatform_ConfigureWindow(width, height, mode, refresh);
    if (mode == MAC_BORDERLESS && haveMonitor) {
        // Present stretches the chosen back buffer to the windowed client area.
        windowWidth = monitor.rcMonitor.right - monitor.rcMonitor.left;
        windowHeight = monitor.rcMonitor.bottom - monitor.rcMonitor.top;
    }
    if (!sdl_gl_window) {
        sdl_gl_window = SDL_CreateWindow("Call of Duty 2", x, y, windowWidth, windowHeight,
                                         SDL_WINDOW_HIDDEN | (mode != MAC_WINDOWED ? SDL_WINDOW_BORDERLESS : 0));
        if (!sdl_gl_window) {
            fprintf(stderr, "Couldn't create the game window: %s\n", SDL_GetError());
            return NULL;
        }
    } else {
        SDL_SetWindowBordered(sdl_gl_window, mode == MAC_WINDOWED ? SDL_TRUE : SDL_FALSE);
        SDL_SetWindowSize(sdl_gl_window, windowWidth, windowHeight);
    }
    if (mode == MAC_WINDOWED) {
        int top = 0, left = 0;
        RECT title = {Dvar_GetInt("vid_xpos"), Dvar_GetInt("vid_ypos"), 0, 0};
        RECT visible;
        SDL_GetWindowBordersSize(sdl_gl_window, &top, &left, NULL, NULL);
        title.right = title.left + width;
        title.bottom = title.top + (top > 0 ? top : 1);
        if (haveMonitor && IntersectRect(&visible, &title, &monitor.rcWork)) {
            x = title.left + left;
            y = title.top + top;
        } else if (haveMonitor) {
            x = monitor.rcWork.left + (monitor.rcWork.right - monitor.rcWork.left - width) / 2;
            y = monitor.rcWork.top + (monitor.rcWork.bottom - monitor.rcWork.top - height) / 2;
        }
    } else if (haveMonitor) {
        x = monitor.rcMonitor.left;
        y = monitor.rcMonitor.top;
    }
    SDL_SetWindowPosition(sdl_gl_window, x, y);
    SDL_ShowWindow(sdl_gl_window);
    SDL_RaiseWindow(sdl_gl_window);
    SDL_VERSION(&info.version);
    if (!SDL_GetWindowWMInfo(sdl_gl_window, &info)) {
        fprintf(stderr, "Couldn't get the game window's handle: %s\n", SDL_GetError());
        return NULL;
    }
    return info.info.win.window;
}

#else
/* The window the device presents to. It keeps SDL's window (and with it SDL's
 * input) and has no OpenGL context. Exclusive fullscreen belongs to the device:
 * the window only loses its border, and SDL never switches modes itself. */
void *Win_D3D9_PrepareWindow(int width, int height, int fullscreen, int refresh)
{
    SDL_SysWMinfo info;

    if (MacDisplay_Initialize() != 0)
        return NULL;
    MacPlatform_ConfigureWindow(width, height, MAC_WINDOWED, refresh);
    if (!sdl_gl_window) {
        sdl_gl_window = SDL_CreateWindow("Call of Duty 2", SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
                                         width, height, SDL_WINDOW_SHOWN | (fullscreen ? SDL_WINDOW_BORDERLESS : 0));
        if (!sdl_gl_window) {
            fprintf(stderr, "Couldn't create the game window: %s\n", SDL_GetError());
            return NULL;
        }
    } else {
        SDL_SetWindowBordered(sdl_gl_window, fullscreen ? SDL_FALSE : SDL_TRUE);
        SDL_SetWindowSize(sdl_gl_window, width, height);
        SDL_ShowWindow(sdl_gl_window);
    }
    if (fullscreen)
        SDL_SetWindowPosition(sdl_gl_window, 0, 0);
    else
        SDL_SetWindowPosition(sdl_gl_window, SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED);
    SDL_RaiseWindow(sdl_gl_window);
    SDL_VERSION(&info.version);
    if (!SDL_GetWindowWMInfo(sdl_gl_window, &info)) {
        fprintf(stderr, "Couldn't get the game window's handle: %s\n", SDL_GetError());
        return NULL;
    }
    return info.info.win.window;
}

#endif

/* r_init.c describes the presentation in fourteen 32-bit words: width, height,
 * back buffer format and count, multisample type and quality, swap effect,
 * windowed, depth format, flags, display mode, presentation interval, refresh
 * rate and a spare. This makes the system structure, which has the window
 * handle in the middle and pointer alignment on x86_64. */
void Win_D3D9_PresentParameters(const int *words, void *window, void *out)
{
    D3DPRESENT_PARAMETERS *parameters = out;

    memset(parameters, 0, sizeof(*parameters));
    parameters->BackBufferWidth = (UINT)words[0];
    parameters->BackBufferHeight = (UINT)words[1];
    parameters->BackBufferFormat = (D3DFORMAT)words[2];
    parameters->BackBufferCount = (UINT)words[3];
    parameters->MultiSampleType = (D3DMULTISAMPLE_TYPE)words[4];
    parameters->MultiSampleQuality = (DWORD)words[5];
    parameters->SwapEffect = (D3DSWAPEFFECT)words[6];
    parameters->hDeviceWindow = (HWND)window;
    parameters->Windowed = words[7] != 0;
#if COD2_WINDOWS_NATIVE
    /* The renderer creates its own depth-stencil surface in R_InitRenderTargets. */
    parameters->EnableAutoDepthStencil = FALSE;
#else
    parameters->EnableAutoDepthStencil = TRUE;
#endif
    parameters->AutoDepthStencilFormat = (D3DFORMAT)words[8];
    parameters->Flags = (DWORD)words[9];
    parameters->FullScreen_RefreshRateInHz = words[7] ? 0 : (UINT)words[12];
    parameters->PresentationInterval = (UINT)words[11];
}

_Static_assert(sizeof(D3DPRESENT_PARAMETERS) <= 16 * sizeof(void *), "r_init.c reserves 16 pointers");

/* Shaders. The Windows game compiles the HLSL in materials/shaders with
 * D3DXCompileShader as each material loads. d3dx9 is not part of Windows, so
 * this uses the compiler that is, d3dcompiler_47. It takes the same source,
 * entry points and vs_2_0/ps_2_0 profiles; ENABLE_BACKWARDS_COMPATIBILITY lets
 * it accept the shaders' half-precision globals, as D3DX did. */
static D3DCompileFn Win_D3D9_Compiler(void)
{
    static const char *const names[] = { "d3dcompiler_47.dll", "d3dcompiler_46.dll", "d3dcompiler_43.dll" };
    static D3DCompileFn compile;
    static int tried;
    if (!tried) {
        tried = 1;
        for (size_t i = 0; i < sizeof(names) / sizeof(names[0]) && !compile; ++i) {
            HMODULE module = LoadLibraryA(names[i]);
            if (module)
                compile = (D3DCompileFn)(void *)GetProcAddress(module, "D3DCompile");
        }
        if (!compile)
            fprintf(stderr, "No HLSL compiler: d3dcompiler_47.dll is missing\n");
    }
    return compile;
}

/* The engine's include handler (r_material_load_obj.c) has ID3DInclude's two
 * methods and resolves names in the preloaded shader text, then under lib/. */
extern HRESULT IncludeClass_Open(const void *self, int type, const char *name, const void *parent,
                                 const void **data, void *bytes);
extern HRESULT IncludeClass_Close(const void *self, const void *data);
static void *const includeVtable[2] = { (void *)IncludeClass_Open, (void *)IncludeClass_Close };
static const void *const includeObject = includeVtable;

HRESULT Win_D3D9_CompileShader(const char *source, int length, const char *name, const char *entry,
                               const char *target, void **shader, void **messages)
{
    static const D3D_SHADER_MACRO defines[] = { { "PC", "" }, { "IW_HLSL", "1" }, { NULL, NULL } };
    D3DCompileFn compile = Win_D3D9_Compiler();

    *shader = NULL;
    if (messages)
        *messages = NULL;
    if (!compile)
        return E_NOTIMPL;
    return compile(source, (SIZE_T)length, name, defines, (ID3DInclude *)&includeObject, entry, target,
                   D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY, 0, (ID3DBlob **)shader, (ID3DBlob **)messages);
}

/* ID3DXConstantTable as the renderer uses it: GetBufferPointer returns the CTAB
 * block, which Material_ParseShaderArguments reads itself, and Release. */
typedef struct {
    const void *const *vtable;
    LONG references;
    DWORD size;
    BYTE data[];
} ConstantTable;

static HRESULT WINAPI ConstantTable_QueryInterface(ConstantTable *self, REFIID iid, void **out)
{
    (void)self;
    (void)iid;
    *out = NULL;
    return E_NOINTERFACE;
}
static ULONG WINAPI ConstantTable_AddRef(ConstantTable *self)
{
    return (ULONG)InterlockedIncrement(&self->references);
}
static ULONG WINAPI ConstantTable_Release(ConstantTable *self)
{
    LONG references = InterlockedDecrement(&self->references);
    if (!references)
        free(self);
    return (ULONG)references;
}
static void *WINAPI ConstantTable_GetBufferPointer(ConstantTable *self)
{
    return self->data;
}
static DWORD WINAPI ConstantTable_GetBufferSize(ConstantTable *self)
{
    return self->size;
}
static const void *const constantTableVtable[] = {
    (void *)ConstantTable_QueryInterface, (void *)ConstantTable_AddRef, (void *)ConstantTable_Release,
    (void *)ConstantTable_GetBufferPointer, (void *)ConstantTable_GetBufferSize,
};

#define CTAB_FOURCC 0x42415443u /* "CTAB" */

HRESULT Win_D3D9_GetShaderConstantTable(const void *function, void **table)
{
    const DWORD *token = function;
    const DWORD *data = NULL;
    DWORD size = 0;
    ConstantTable *result;

    *table = NULL;
    if (!token || (*token & 0xfffe0000u) != 0xfffe0000u)
        return D3DERR_INVALIDCALL;
    /* Walk the token stream to the CTAB comment, which compilers put first. */
    for (++token; *token != 0x0000ffffu;) {
        DWORD opcode = *token & 0xffffu;
        if (opcode == 0xfffeu) {
            DWORD length = (*token >> 16) & 0x7fffu;
            if (length >= 1 && token[1] == CTAB_FOURCC) {
                data = token + 2;
                size = (length - 1) * 4;
                break;
            }
            token += length + 1;
        } else {
            token += ((*token >> 24) & 0xfu) + 1;
        }
    }
    if (!data || size < 28)
        return (HRESULT)0x88760b59; /* D3DXERR_INVALIDDATA */
    result = malloc(sizeof(*result) + size);
    if (!result)
        return E_OUTOFMEMORY;
    result->vtable = constantTableVtable;
    result->references = 1;
    result->size = size;
    memcpy(result->data, data, size);
    *table = result;
    return S_OK;
}

/* Screenshots: the back buffer's top left width x height pixels as rows of
 * red, green, blue and alpha bytes, top row first, before Present. */
int Win_D3D9_ReadBackBuffer(void *deviceObject, int width, int height, unsigned char *rgba)
{
    IDirect3DDevice9 *device = deviceObject;
    IDirect3DSurface9 *backBuffer = NULL, *resolved = NULL, *copy = NULL;
    D3DSURFACE_DESC desc;
    D3DLOCKED_RECT locked;
    HRESULT hr;
    int ok = 0;

    hr = IDirect3DDevice9_GetBackBuffer(device, 0, 0, D3DBACKBUFFER_TYPE_MONO, &backBuffer);
    if (FAILED(hr))
        return 0;
    IDirect3DSurface9_GetDesc(backBuffer, &desc);
    if ((desc.Format != D3DFMT_A8R8G8B8 && desc.Format != D3DFMT_X8R8G8B8) ||
        (UINT)width > desc.Width || (UINT)height > desc.Height)
        goto done;
    resolved = backBuffer;
    IDirect3DSurface9_AddRef(resolved);
    if (desc.MultiSampleType != D3DMULTISAMPLE_NONE) {
        IDirect3DSurface9_Release(resolved);
        resolved = NULL;
        hr = IDirect3DDevice9_CreateRenderTarget(device, desc.Width, desc.Height, desc.Format,
                                                 D3DMULTISAMPLE_NONE, 0, FALSE, &resolved, NULL);
        if (FAILED(hr) ||
            FAILED(IDirect3DDevice9_StretchRect(device, backBuffer, NULL, resolved, NULL, D3DTEXF_NONE)))
            goto done;
    }
    hr = IDirect3DDevice9_CreateOffscreenPlainSurface(device, desc.Width, desc.Height, desc.Format,
                                                      D3DPOOL_SYSTEMMEM, &copy, NULL);
    if (FAILED(hr) || FAILED(IDirect3DDevice9_GetRenderTargetData(device, resolved, copy)) ||
        FAILED(IDirect3DSurface9_LockRect(copy, &locked, NULL, D3DLOCK_READONLY)))
        goto done;
    for (int y = 0; y < height; ++y) {
        const unsigned char *src = (const unsigned char *)locked.pBits + (size_t)y * locked.Pitch;
        unsigned char *dst = rgba + (size_t)y * width * 4;
        for (int x = 0; x < width; ++x, src += 4, dst += 4) {
            dst[0] = src[2];
            dst[1] = src[1];
            dst[2] = src[0];
            dst[3] = desc.Format == D3DFMT_X8R8G8B8 ? 0xff : src[3];
        }
    }
    IDirect3DSurface9_UnlockRect(copy);
    ok = 1;
done:
    if (copy)
        IDirect3DSurface9_Release(copy);
    if (resolved)
        IDirect3DSurface9_Release(resolved);
    IDirect3DSurface9_Release(backBuffer);
    return ok;
}
