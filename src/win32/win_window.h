#ifndef COD2_WIN_WINDOW_H
#define COD2_WIN_WINDOW_H

struct SDL_SysWMmsg;
enum WinInputKind {
    WIN_INPUT_NONE, WIN_INPUT_KEY, WIN_INPUT_CONTROL, WIN_INPUT_WHEEL, WIN_INPUT_ALT_ENTER
};

void Win_InputEnableMessages(void);
enum WinInputKind Win_InputMessage(const struct SDL_SysWMmsg *message, int *value, int *down);
char *Win_InputTextToAnsi(const char *utf8);

#endif
