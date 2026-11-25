/* Print module-relative PCs from the kit's own x64 client. No dump or symbols
 * are written; only addresses from the captured stacks leave the process. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dbghelp.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

static void print_frame(HANDLE process, DWORD64 pc)
{
    IMAGEHLP_MODULE64 module = {0};
    module.SizeOfStruct = sizeof(module);
    if (SymGetModuleInfo64(process, pc, &module))
        printf("  %s+0x%llx\n", module.ModuleName, (unsigned long long)(pc - module.BaseOfImage));
    else
        printf("  0x%llx\n", (unsigned long long)pc);
}

static int print_thread(HANDLE process, DWORD pid, DWORD tid)
{
    HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT |
                               THREAD_QUERY_LIMITED_INFORMATION, FALSE, tid);
    if (!thread)
        return 0;
    if (GetProcessIdOfThread(thread) != pid || SuspendThread(thread) == (DWORD)-1) {
        CloseHandle(thread);
        return 0;
    }

    CONTEXT context = {0};
    context.ContextFlags = CONTEXT_FULL;
    DWORD64 pcs[32] = {0};
    unsigned count = 0;
    if (GetThreadContext(thread, &context)) {
        STACKFRAME64 frame = {0};
        frame.AddrPC.Offset = context.Rip;
        frame.AddrFrame.Offset = context.Rbp;
        frame.AddrStack.Offset = context.Rsp;
        frame.AddrPC.Mode = frame.AddrFrame.Mode = frame.AddrStack.Mode = AddrModeFlat;
        pcs[count++] = context.Rip;
        while (count < ARRAYSIZE(pcs) &&
               StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, thread, &frame, &context,
                           NULL, SymFunctionTableAccess64, SymGetModuleBase64, NULL) &&
               frame.AddrPC.Offset != 0) {
            pcs[count++] = frame.AddrPC.Offset;
        }
    }
    ResumeThread(thread);
    CloseHandle(thread);

    printf("Thread %lu\n", (unsigned long)tid);
    for (unsigned index = 0; index < count; ++index)
        print_frame(process, pcs[index]);
    if (!count)
        puts("  Context unavailable.");
    return count != 0;
}

static BOOL is_kit_client(HANDLE process, ULONGLONG start)
{
    FILETIME created, exited, idle, active;
    wchar_t path[MAX_PATH];
    DWORD size = ARRAYSIZE(path);
    if (!GetProcessTimes(process, &created, &exited, &idle, &active) ||
        (((ULONGLONG)created.dwHighDateTime << 32) | created.dwLowDateTime) != start ||
        !QueryFullProcessImageNameW(process, 0, path, &size))
        return FALSE;
    const wchar_t *leaf = wcsrchr(path, L'\\');
    return leaf && _wcsicmp(leaf + 1, L"cod2_win32.exe") == 0;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc != 3)
        return 2;
    DWORD pid = wcstoul(argv[1], NULL, 10);
    ULONGLONG start = _wcstoui64(argv[2], NULL, 10);
    HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
    if (!process)
        return 2;
    int captured = 0;
    if (is_kit_client(process, start)) {
        wchar_t symbol_path[MAX_PATH];
        DWORD size = GetCurrentDirectoryW(ARRAYSIZE(symbol_path), symbol_path);
        SymSetOptions(SYMOPT_IGNORE_NT_SYMPATH | SYMOPT_NO_PROMPTS |
                      SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_DEFERRED_LOADS);
        if (size && size < ARRAYSIZE(symbol_path) && SymInitializeW(process, symbol_path, TRUE)) {
            HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snapshot != INVALID_HANDLE_VALUE) {
                THREADENTRY32 entry = {0};
                entry.dwSize = sizeof(entry);
                if (Thread32First(snapshot, &entry)) {
                    do {
                        if (entry.th32OwnerProcessID == pid)
                            captured += print_thread(process, pid, entry.th32ThreadID);
                        entry.dwSize = sizeof(entry);
                    } while (Thread32Next(snapshot, &entry));
                }
                CloseHandle(snapshot);
            }
            SymCleanup(process);
        }
    }
    CloseHandle(process);
    return captured ? 0 : 1;
}
