/* The launcher can transmit this separate report. The older engine diagnostics
 * are for local support and may contain paths, cvars, registers or raw stack data. */
#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <stdint.h>
#include <stdio.h>
#include <wchar.h>
#include "win_launcher_crash.h"
#include "win_files.h"

static int SafeModuleName(const wchar_t *name)
{
    size_t length = wcslen(name);
    if (length < 5 || length > 64 ||
        (_wcsicmp(name + length - 4, L".dll") && _wcsicmp(name + length - 4, L".exe") &&
         _wcsicmp(name + length - 4, L".drv")))
        return 0;
    for (; *name; ++name) {
        wchar_t c = *name;
        if (!((c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') ||
              (c >= L'0' && c <= L'9') || c == L'_' || c == L'-' || c == L'.'))
            return 0;
    }
    return 1;
}

static const wchar_t *ModuleName(const wchar_t *path, const wchar_t *system, HMODULE module)
{
    if (module == GetModuleHandleW(NULL))
        return L"engine.exe";
    size_t length = wcslen(system);
    const wchar_t *name = wcsrchr(path, L'\\');
    return name && length && !_wcsnicmp(path, system, length) && path[length] == L'\\' && SafeModuleName(name + 1)
        ? name + 1 : L"unknown";
}

static void WriteFrame(FILE *out, const char *label, DWORD64 pc, const wchar_t *system)
{
    HMODULE module = NULL;
    wchar_t path[MAX_PATH];
    DWORD count = 0;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           (LPCWSTR)(uintptr_t)pc, &module))
        count = GetModuleFileNameW(module, path, MAX_PATH);
    fprintf(out, "%s: %ls+0x%llX\n", label, count && count < MAX_PATH ? ModuleName(path, system, module) : L"unknown",
        (unsigned long long)(module ? pc - (uintptr_t)module : 0));
}

static void WriteStack(FILE *out, const CONTEXT *context, const wchar_t *system)
{
    if (!context)
        return;
    HANDLE process = GetCurrentProcess();
    CONTEXT copy = *context;
    STACKFRAME64 frame = {0};
#if defined(_WIN64)
    const DWORD machine = IMAGE_FILE_MACHINE_AMD64;
    frame.AddrPC.Offset = copy.Rip;
    frame.AddrFrame.Offset = copy.Rbp;
    frame.AddrStack.Offset = copy.Rsp;
#else
    const DWORD machine = IMAGE_FILE_MACHINE_I386;
    frame.AddrPC.Offset = copy.Eip;
    frame.AddrFrame.Offset = copy.Ebp;
    frame.AddrStack.Offset = copy.Esp;
#endif
    frame.AddrPC.Mode = frame.AddrFrame.Mode = frame.AddrStack.Mode = AddrModeFlat;
    if (!SymInitializeW(process, L"", FALSE))
        return;
    /* Resolve unwind data from loaded images only. Do not consult a symbol server. */
    SymSetOptions(SYMOPT_IGNORE_NT_SYMPATH | SYMOPT_NO_PROMPTS | SYMOPT_NO_IMAGE_SEARCH);
    if (!SymRefreshModuleList(process)) {
        SymCleanup(process);
        return;
    }
    for (int depth = 0; depth < 32 && frame.AddrPC.Offset; depth++) {
        WriteFrame(out, "Frame", frame.AddrPC.Offset, system);
        DWORD64 stack = frame.AddrStack.Offset;
        if (!StackWalk64(machine, process, GetCurrentThread(), &frame, &copy, NULL,
                         SymFunctionTableAccess64, SymGetModuleBase64, NULL) || frame.AddrStack.Offset == stack)
            break;
    }
    SymCleanup(process);
}

static void WriteModules(FILE *out, const wchar_t *system)
{
    HANDLE modules = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (modules == INVALID_HANDLE_VALUE)
        return;
    MODULEENTRY32W item = {0};
    item.dwSize = sizeof(item);
    if (Module32FirstW(modules, &item)) {
        for (int count = 0; count < 128; count++) {
            const wchar_t *name = ModuleName(item.szExePath, system, item.hModule);
            DWORD scratch;
            DWORD bytes = GetFileVersionInfoSizeW(item.szExePath, &scratch);
            VS_FIXEDFILEINFO *version = NULL;
            UINT size = 0;
            if (bytes > 0 && bytes <= 128 * 1024) {
                void *data = malloc(bytes);
                if (data && GetFileVersionInfoW(item.szExePath, 0, bytes, data) &&
                    VerQueryValueW(data, L"\\", (void **)&version, &size) && size >= sizeof(*version))
                    fprintf(out, "Module: %ls %u.%u.%u.%u\n", name,
                        HIWORD(version->dwFileVersionMS), LOWORD(version->dwFileVersionMS),
                        HIWORD(version->dwFileVersionLS), LOWORD(version->dwFileVersionLS));
                else
                    fprintf(out, "Module: %ls unknown\n", name);
                free(data);
            } else {
                fprintf(out, "Module: %ls unknown\n", name);
            }
            if (!Module32NextW(modules, &item))
                break;
        }
    }
    CloseHandle(modules);
}

void Win_WriteLauncherCrash(EXCEPTION_POINTERS *exception)
{
    char name[100], temporary[105];
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    snprintf(name, sizeof(name), "cod2_unbound_crash_%08lX%08lX_%lu.txt", (unsigned long)now.dwHighDateTime,
        (unsigned long)now.dwLowDateTime, (unsigned long)GetCurrentProcessId());
    snprintf(temporary, sizeof(temporary), "%s.tmp", name);
    FILE *out = Win_FileOpen(temporary, "wb");
    if (!out)
        return;

    wchar_t system[MAX_PATH] = {0};
    UINT length = GetSystemDirectoryW(system, MAX_PATH);
    if (length >= MAX_PATH)
        system[0] = 0;
    CONTEXT captured;
    EXCEPTION_RECORD *record = exception ? exception->ExceptionRecord : NULL;
    DWORD code = record ? record->ExceptionCode : 0x40000015UL;
    uintptr_t pc;
    const CONTEXT *context = exception ? exception->ContextRecord : &captured;
    if (record) {
        pc = (uintptr_t)record->ExceptionAddress;
    } else {
        RtlCaptureContext(&captured);
#if defined(_WIN64)
        pc = captured.Rip;
#else
        pc = captured.Eip;
#endif
    }
    fprintf(out, "CoD2 Unbound Windows crash 1\nException: 0x%08lX at 0x%llX\n",
        (unsigned long)code, (unsigned long long)pc);
    WriteFrame(out, "Fault", pc, system);
    WriteStack(out, context, system);
    WriteModules(out, system);
    fputs("End\n", out);
    int failed = ferror(out);
    if (fclose(out) == 0 && !failed && Win_Rename(temporary, name) == 0)
        return;
    Win_Remove(temporary);
}
