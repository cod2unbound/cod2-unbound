/* Offline replay hooks for the SHA-256-pinned Windows 1.3 retail PE. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static HANDLE recorder_log;

static void log_message(const char *message)
{
    DWORD written;
    WriteFile(recorder_log, message, (DWORD)strlen(message), &written, NULL);
}

static void fail(const char *message)
{
    log_message(message);
    ExitProcess(2);
}

static void redirect_call(uint32_t address, uint32_t original, const void *target)
{
    unsigned char *site = (unsigned char *)(uintptr_t)address;
    uint32_t displacement;
    DWORD old;
    memcpy(&displacement, site + 1, sizeof displacement);
    if (site[0] != 0xe8 || address + 5 + displacement != original ||
        !VirtualProtect(site, 5, PAGE_EXECUTE_READWRITE, &old))
        fail("Retail recorder: unexpected call instruction or inaccessible code\n");
    displacement = (uint32_t)(uintptr_t)target - address - 5;
    memcpy(site + 1, &displacement, sizeof displacement);
    if (!VirtualProtect(site, 5, old, &old) ||
        !FlushInstructionCache(GetCurrentProcess(), site, 5))
        fail("Retail recorder: could not restore or flush instruction memory\n");
}

enum {
    ENTITY_BYTES = 240,
    ENTITY_STRIDE = 560,
    ENTITY_INUSE = 252,
    ENTITY_CLIENT = 344,
    CLIENT_STRIDE = 725092,
    CLIENT_DELTA = 134680,
    CLIENT_NET_SEQUENCE = 452292,
    CLIENT_IS_BOT = 714508,
    PLAYER_BYTES = 304,
};
#define READ_I32(addr) (*(const int32_t *)(uintptr_t)(addr))
#define READ_PTR(addr) (*(unsigned char *const *)(uintptr_t)(addr))
#define LEVEL_ENTITIES READ_I32(0x193a78c)
#define LEVEL_LOG_FILE READ_I32(0x193a798)
#define LEVEL_CLIENTS  READ_I32(0x193a964)
#define LEVEL_FRAME    READ_I32(0x193a968)
#define LEVEL_TIME     READ_I32(0x193a96c)
#define LEVEL_START    READ_I32(0x193a978)
#define SERVER_CLIENTS READ_PTR(0x00d3570c)
#define GAME_ENTITIES  ((unsigned char *)0x01744380)

typedef struct {
    int32_t time;
    uint32_t buttons;
    uint8_t weapon;
    uint8_t offhand;
    int32_t angles[3];
    int8_t forward;
    int8_t right;
} Command;

_Static_assert(sizeof(Command) == 28, "retail command layout");

static FILE *commands;
static FILE *capture;
static int replay_ticks, replay_commands, replay_clients;
static int replay_seed __attribute__((used));
static int next_frame, next_client, end_frame, injected, captured;
static Command next_cmd;

static void read_command(void)
{
    char line[256], extra;
    int frame, client, time, weapon, offhand, a0, a1, a2, forward, right;
    unsigned buttons;
    if (!fgets(line, sizeof line, commands))
        fail("Replay input has no end record\n");
    if (sscanf(line, "end %d %c", &frame, &extra) == 1) {
        if (frame < next_frame || frame < 1 || fgetc(commands) != EOF)
            fail("Invalid replay end record\n");
        end_frame = frame;
        next_frame = 0;
        return;
    }
    if (sscanf(line, "cmd %d %d %d %u %d %d %d %d %d %d %d %c",
               &frame, &client, &time, &buttons, &weapon, &offhand,
               &a0, &a1, &a2, &forward, &right, &extra) != 11 ||
        frame < 1 || frame < next_frame || client < 0 || client >= 64 ||
        weapon < 0 || weapon > 255 || offhand < 0 || offhand > 255 ||
        forward < -128 || forward > 127 || right < -128 || right > 127)
        fail("Invalid replay command\n");
    memset(&next_cmd, 0, sizeof next_cmd);
    next_frame = frame;
    next_client = client;
    next_cmd.time = time;
    next_cmd.buttons = buttons;
    next_cmd.weapon = (uint8_t)weapon;
    next_cmd.offhand = (uint8_t)offhand;
    next_cmd.angles[0] = a0;
    next_cmd.angles[1] = a1;
    next_cmd.angles[2] = a2;
    next_cmd.forward = (int8_t)forward;
    next_cmd.right = (int8_t)right;
}

static void __attribute__((used)) inject_replay(void)
{
    const int frame = LEVEL_FRAME + 1;
    if (!commands) {
        char header[32];
        commands = fopen("..\\usercmds.txt", "r");
        capture = fopen("..\\capture.bin", "wb");
        if (!commands || !capture || !fgets(header, sizeof header, commands) ||
            strcmp(header, "cod2-replay 1\n"))
            fail("Cannot open the replay input or output\n");
        read_command();
    }
    if ((next_frame && next_frame < frame) || (end_frame && frame > end_frame))
        fail("Replay skipped an input tick\n");
    while (next_frame == frame) {
        Command cmd = next_cmd;
        Command *argument = &cmd;
        unsigned char *client;
        int64_t time = (int64_t)cmd.time + LEVEL_START;
        if (!SERVER_CLIENTS || next_client >= LEVEL_CLIENTS ||
            time < INT32_MIN || time > INT32_MAX)
            fail("Replay input has an invalid client or command time\n");
        client = SERVER_CLIENTS + next_client * CLIENT_STRIDE;
        if (READ_I32(client) != 4 || !READ_I32(client + CLIENT_IS_BOT))
            fail("Replay input targets a client that is not an active test client\n");
        cmd.time = (int32_t)time;
        *(int32_t *)(client + CLIENT_DELTA) = READ_I32(client + CLIENT_NET_SEQUENCE) - 1;
        /* VC 7.1 LTCG passes the client in ECX and command in EAX. */
        __asm__ volatile ("call *%[target]"
                          : "+a"(argument), "+c"(client)
                          : [target] "r"((uintptr_t)0x456010)
                          : "edx", "cc", "memory");
        ++injected;
        read_command();
    }
}

static void write_capture(const void *data, size_t size)
{
    if (fwrite(data, 1, size, capture) != size)
        fail("Capture write failed\n");
}

static void __attribute__((used)) snapshot_tick(void)
{
    int32_t header[4] = {LEVEL_FRAME, LEVEL_TIME - LEVEL_START, 0, 0};
    int entities = LEVEL_ENTITIES;
    int slots = LEVEL_CLIENTS;
    if (!capture || entities <= 0 || entities > 1024 || slots < 0 || slots > 64)
        fail("Unexpected active entity or player slot count\n");
    for (int i = 0; i < entities; ++i)
        if (GAME_ENTITIES[i * ENTITY_STRIDE + ENTITY_INUSE])
            ++header[2];
    for (int i = 0; SERVER_CLIENTS && i < slots; ++i) {
        unsigned char *entity = GAME_ENTITIES + i * ENTITY_STRIDE;
        if (READ_I32(SERVER_CLIENTS + i * CLIENT_STRIDE) == 4 &&
            entity[ENTITY_INUSE] && READ_PTR(entity + ENTITY_CLIENT))
            ++header[3];
    }
    write_capture(header, sizeof header);
    for (int32_t i = 0; i < entities; ++i) {
        unsigned char *entity = GAME_ENTITIES + i * ENTITY_STRIDE;
        if (entity[ENTITY_INUSE]) {
            write_capture(&i, sizeof i);
            write_capture(entity, ENTITY_BYTES);
        }
    }
    for (int32_t i = 0; SERVER_CLIENTS && i < slots; ++i) {
        unsigned char *entity = GAME_ENTITIES + i * ENTITY_STRIDE;
        unsigned char *ps = READ_PTR(entity + ENTITY_CLIENT);
        if (READ_I32(SERVER_CLIENTS + i * CLIENT_STRIDE) == 4 && entity[ENTITY_INUSE] && ps) {
            write_capture(&i, sizeof i);
            write_capture(ps, PLAYER_BYTES);
        }
    }
    if (++captured == replay_ticks) {
        char message[128];
        if (next_frame || end_frame != LEVEL_FRAME || injected != replay_commands || header[3] != replay_clients ||
            fclose(capture))
            fail("Replay did not finish with the expected end tick, commands and active clients\n");
        snprintf(message, sizeof message, "Retail recorder wrote %d ticks and injected %d commands for %d clients\n",
                 captured, injected, header[3]);
        log_message(message);
        /* ExitProcess bypasses the game's buffered log shutdown. G_LogPrintf
           reads this handle at 0x4fcb50 and FS_FCloseFile takes it on the stack. */
        if (LEVEL_LOG_FILE) {
            void (__cdecl *close_game_file)(int) = (void *)0x421fd0;
            close_game_file(LEVEL_LOG_FILE);
        }
        ExitProcess(0);
    }
}

/* Called from the shared SV_RunFrame wrapper, including during map startup.
 * Only the call from SV_Frame represents a replay tick. The return address
 * from SV_Frame is two stack slots above this hook's return address. */
static void __attribute__((naked)) capture_game_tick(void)
{
    __asm__("cmpl $0x45c889, 8(%esp)\n\t"
            "jne 1f\n\t"
            "pushal\n\t"
            "call _inject_replay\n\t"
            "popal\n\t"
            "movl $0x4fd1b0, %edx\n\t"
            "call *%edx\n\t"
            "pushfl\n\t"
            "pushal\n\t"
            "call _snapshot_tick\n\t"
            "popal\n\t"
            "popfl\n\t"
            "ret\n"
            "1: pushl $0x4fd1b0\n\t"
            "ret");
}

static void __attribute__((naked)) fixed_game_seed(void)
{
    __asm__("movl _replay_seed, %esi\n\t"
            "movl %esi, 4(%esp)\n\t"
            "pushl $0x57baa0\n\t"
            "ret");
}

static void suppress_bot_move(void) {}

static int replay_setting(const char *name, int minimum, int maximum)
{
    char value[32], *end;
    DWORD length = GetEnvironmentVariableA(name, value, sizeof value);
    long long parsed;
    if (!length || length >= sizeof value || value[0] < '0' || value[0] > '9')
        fail("Missing or invalid retail replay setting\n");
    parsed = strtoll(value, &end, 10);
    if (*end || parsed < minimum || parsed > maximum)
        fail("Retail replay setting is out of range\n");
    return (int)parsed;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    char enabled[2];
    (void)instance;
    (void)reserved;
    if (reason != DLL_PROCESS_ATTACH ||
        GetEnvironmentVariableA("COD2_RETAIL_REPLAY", enabled, sizeof enabled) != 1 ||
        enabled[0] != '1')
        return TRUE;
    recorder_log = CreateFileA("..\\recorder.log", GENERIC_WRITE, FILE_SHARE_READ,
                               NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (recorder_log == INVALID_HANDLE_VALUE)
        return FALSE;
    if ((uintptr_t)GetModuleHandleA(NULL) != 0x00400000)
        fail("Retail recorder: unexpected image base\n");
    replay_ticks = replay_setting("COD2_RETAIL_TICKS", 1, INT32_MAX);
    replay_commands = replay_setting("COD2_RETAIL_COMMANDS", 1, INT32_MAX);
    replay_clients = replay_setting("COD2_RETAIL_CLIENTS", 1, 64);
    replay_seed = replay_setting("COD2_RETAIL_SEED", 0, INT32_MAX);
    redirect_call(0x45c1ff, 0x4fd1b0, capture_game_tick);
    redirect_call(0x4fc305, 0x57baa0, fixed_game_seed);
    redirect_call(0x45c525, 0x45c210, suppress_bot_move);
    log_message("Retail recorder installed\n");
    return TRUE;
}
