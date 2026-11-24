#ifndef COD2_WIN_FRAME_TRACE_H
#define COD2_WIN_FRAME_TRACE_H

#include <stdbool.h>
#include <stdint.h>

extern bool Win_FrameTraceActive;
void Win_FrameTraceInit(void);
uint64_t Win_FrameTraceBegin(void);
void Win_FrameTraceEnd(uint64_t start);

#endif
