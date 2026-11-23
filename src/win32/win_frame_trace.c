#include "headers/cod2_platform.h"

#if COD2_WINDOWS_NATIVE
#include "win_frame_trace.h"
#include <stdio.h>
#include <stdlib.h>
#include <windows.h>
#include <wchar.h>

/* Keep the most recent presents in memory. The capture header counts all
 * successes so the reader can detect a wrapped trace. */
enum { TRACE_CAPACITY = 262144 };
typedef struct {
    uint64_t start;
    uint64_t duration;
} PresentSample;

bool Win_FrameTraceActive;
static PresentSample *samples;
static wchar_t *tracePath;
static uint64_t frequency;
static uint64_t total;

static void Win_FrameTraceWrite(void)
{
    FILE *file = _wfopen(tracePath, L"wb");
    if (!file) {
        fputs("Could not open the Windows frame trace output\n", stderr);
        return;
    }

    const uint64_t metadata[] = { frequency, total };
    size_t first = total > TRACE_CAPACITY ? total % TRACE_CAPACITY : 0;
    size_t count = total > TRACE_CAPACITY ? TRACE_CAPACITY : (size_t)total;
    size_t tail = count - first;
    bool written = fwrite("COD2QPC1", 1, 8, file) == 8 &&
                   fwrite(metadata, sizeof(metadata), 1, file) == 1 &&
                   fwrite(samples + first, sizeof(*samples), tail, file) == tail &&
                   fwrite(samples, sizeof(*samples), first, file) == first;
    if (fclose(file) != 0 || !written)
        fputs("Could not write the Windows frame trace\n", stderr);
}

void Win_FrameTraceInit(void)
{
    static bool initialized;
    if (initialized)
        return;
    initialized = true;

    const wchar_t *path = _wgetenv(L"COD2_FRAME_TRACE_FILE");
    if (!path || !*path)
        return;

    LARGE_INTEGER counterFrequency;
    if (!QueryPerformanceFrequency(&counterFrequency))
        return;

    tracePath = _wcsdup(path);
    const size_t bytes = TRACE_CAPACITY * sizeof(*samples);
    samples = VirtualAlloc(NULL, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!tracePath || !samples || atexit(Win_FrameTraceWrite) != 0) {
        if (samples)
            VirtualFree(samples, 0, MEM_RELEASE);
        free(tracePath);
        fputs("Could not initialize the Windows frame trace\n", stderr);
        return;
    }

    /* Fault in the buffer before rendering so a new page cannot delay Present. */
    SYSTEM_INFO system;
    GetSystemInfo(&system);
    volatile unsigned char *buffer = (volatile unsigned char *)samples;
    for (size_t offset = 0; offset < bytes; offset += system.dwPageSize)
        buffer[offset] = 0;

    frequency = (uint64_t)counterFrequency.QuadPart;
    Win_FrameTraceActive = true;
}

uint64_t Win_FrameTraceBegin(void)
{
    LARGE_INTEGER counter;
    QueryPerformanceCounter(&counter);
    return (uint64_t)counter.QuadPart;
}

void Win_FrameTraceEnd(uint64_t start)
{
    uint64_t end = Win_FrameTraceBegin();
    samples[total % TRACE_CAPACITY] = (PresentSample){ start, end - start };
    ++total;
}
#endif
