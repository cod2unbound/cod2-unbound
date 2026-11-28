/* Loopback-only client for the Windows snapshot oracle. */
#include "common_types.h"
#include "PC/qcommon/qcommon_funcs.h"
#include <errno.h>
#include <inttypes.h>
#include <stddef.h>
#include <stdarg.h>
#include <stdlib.h>
#include <time.h>
#include <unistd.h>

extern void CL_Netchan_Decode(byte *, int);
extern void CL_ParseServerMessage(msg_t *);
extern void CL_WritePacket(void);

static clientActive_t active;
static clientConnection_t conn;
static clientActive_t *active_ptr = &active;
static clientConnection_t *conn_ptr = &conn;
static dvar_t zero_dvar, shownet = {.current.integer = 3};
static int udp;
static FILE *datagrams;
struct clientStatic_t cls;
void *cl = &active;
void *clc_ptr = &conn_ptr, *cl_ptr = &active_ptr, *cls_ptr = &cls;
const dvar_t *cl_shownet = &shownet;
void *imp_clc = &conn_ptr, *imp_cl = &active_ptr, *imp_cl_shownet = &cl_shownet;
const dvar_t *cl_paused = &zero_dvar, *cl_shownuments = &zero_dvar, *com_sv_running = &zero_dvar;
const dvar_t *cl_showSend = &zero_dvar, *cl_nodelta = &zero_dvar, *cl_packetdup = &zero_dvar;
const dvar_t *net_profile = &zero_dvar, *net_showprofile = &zero_dvar, *net_lanauthorize = &zero_dvar;
const dvar_t *showdrop = &zero_dvar, *showpackets = &zero_dvar, *packetDebug = &zero_dvar;
loopback_t loopbacks[2];

void Com_Error(errorParm_t level, const char *fmt, ...)
{
    va_list args;
    (void)level;
    va_start(args, fmt);
    vfprintf(stderr, fmt, args);
    va_end(args);
    fputc('\n', stderr);
    exit(2);
}

void Com_Printf(const char *fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    vprintf(fmt, args);
    va_end(args);
}

void Com_DPrintf(const char *fmt, ...) { (void)fmt; }
int Sys_Milliseconds(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int)((uint64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000);
}
qboolean Sys_IsLANAddress(netadr_t address) { return address.type == NA_IP && address.ip[0] == 127; }
Bool Sys_SendPacket(int length, const void *data, netadr_t address)
{
    if (!Sys_IsLANAddress(address))
        Com_Error(0, "Receiver only sends to loopback");
    if (send(udp, data, length, 0) == length)
        return 1;
    if (errno == ECONNREFUSED)
        return 0;
    Com_Error(0, "send: %s", strerror(errno));
    return 0;
}

/* The parser uses an engine scratch allocator whose handle is an integer. */
static void *locals[16];
static int local_count;
void LargeLocal_LargeLocal(LargeLocal *place, int size)
{
    if (local_count >= 16 || !(locals[local_count] = malloc(size)))
        Com_Error(0, "Snapshot scratch allocation failed");
    place->_placeholder = local_count++;
}
void *LargeLocal_GetBuf(const LargeLocal *place) { return locals[place->_placeholder]; }
void ZN10LargeLocalD1Ev(LargeLocal *place)
{
    if (place->_placeholder != --local_count)
        Com_Error(0, "Snapshot scratch handles released out of order");
    free(locals[local_count]);
}

void Con_Close(void) {}
void CL_ClearState(void) { memset(&active, 0, sizeof active); }
void CL_InitDownloads(void) { conn.state = CA_PRIMED; }
void CL_RequestAuthorization(void) { Com_Error(0, "Unexpected client authorization request"); }
void MatchSummary_Finish(const char *reason) { (void)reason; }
void MatchSummary_Snapshot(int ping, qboolean measured, qboolean spectator)
{ (void)ping; (void)measured; (void)spectator; }
qboolean FS_ConditionalRestart(int checksum) { (void)checksum; return 0; }
void FS_PureServerSetLoadedIwds(const char *sums, const char *names) { (void)sums; (void)names; }
void FS_PureServerSetReferencedIwds(const char *sums, const char *names) { (void)sums; (void)names; }
Bool Dvar_GetBool(const char *name) { (void)name; return 0; }
void Dvar_SetInt(const dvar_t *dvar, int value) { (void)dvar; (void)value; }
void Dvar_SetFromStringByName(const char *name, const char *value) { (void)name; (void)value; }
void Dvar_SetCheatState(void) {}

/* The stock parser links its download/UI calls even in a LAN-only receiver. */
static LegacyHacks legacy, *legacy_ptr = &legacy;
void *imp_legacyHacks = &legacy_ptr;
int cl_connectedToPureServer;
const dvar_t *com_developer = &zero_dvar;
static byte download_ui[16];
static byte *download_ui_state = download_ui;
byte **download_ui_ptr = &download_ui_state;
static va_info_t va_info;
void *Sys_GetValue(int slot) { return slot == 1 ? &va_info : NULL; }
const dvar_t *Dvar_FindVar(const char *name) { (void)name; return NULL; }
char *Dvar_GetString(const char *name) { (void)name; return ""; }
void CL_AddReliableCommand(const char *cmd) { Com_Error(0, "Unexpected command: %s", cmd); }
void CL_NextDownload(void) { Com_Error(0, "Unexpected download"); }
void Sys_OpenURL(const char *url, int activate) { (void)url; (void)activate; CL_NextDownload(); }
void Cbuf_ExecuteText(int exec, const char *text) { (void)exec; (void)text; CL_NextDownload(); }
void FS_BuildOSPath(const char *base, const char *game, const char *path, char *out)
{ (void)base; (void)game; (void)path; (void)out; CL_NextDownload(); }
fileHandle_t FS_SV_FOpenFileWrite(const char *path) { (void)path; CL_NextDownload(); return 0; }
void FS_SV_Rename(const char *from, const char *to) { (void)from; (void)to; CL_NextDownload(); }
int FS_Write(const void *data, int len, fileHandle_t fd)
{ (void)data; (void)len; (void)fd; CL_NextDownload(); return 0; }
void FS_FCloseFile(fileHandle_t fd) { (void)fd; CL_NextDownload(); }

static void send_oob(const char *command)
{
    char packet[512];
    int length = snprintf(packet, sizeof packet, "\xff\xff\xff\xff%s", command);
    if (length < 4 || length >= (int)sizeof packet)
        Com_Error(0, "Bad connectionless command");
    Sys_SendPacket(length, packet, conn.serverAddress);
}

static void send_command(void)
{
    usercmd_t *cmd = &active.cmds[++active.cmdNumber & 127];
    MSG_SetDefaultUserCmd(&active.snap.ps, cmd);
    cmd->serverTime = active.snap.valid ? active.snap.serverTime : 0;
    CL_WritePacket();
}

/* The decoder zero-initializes these records and stores their fields in
 * 32-bit words. Preserve their exact bits, including quantized floats. */
static void print_words(const void *data, size_t size)
{
    const byte *bytes = data;
    for (size_t offset = 0; offset < size; offset += sizeof(uint32_t)) {
        uint32_t bits;
        memcpy(&bits, bytes + offset, sizeof bits);
        printf(" %08" PRIx32, bits);
    }
    putchar('\n');
}

static void print_hud(const hudelem_t *elements, int count, int first)
{
    const hudelem_t empty = {0};
    for (int i = 0; i < count; ++i) {
        const hudelem_t *hud = &elements[i];
        if (!memcmp(hud, &empty, sizeof *hud))
            continue;
        printf("hud %d", first + i);
        print_words(hud, sizeof *hud);
    }
}

static void print_snapshot(int datagram_size)
{
    const clSnapshot_t *snap = &active.snap;
    const playerState_t *ps = &snap->ps;
    const size_t after_command_time = offsetof(playerState_t, pm_type);
    printf("snapshot %d size %d seq %d delta %d flags %d player",
           snap->serverTime, datagram_size, snap->messageNum, snap->deltaNum, snap->snapFlags);
    print_words((const byte *)ps + after_command_time, offsetof(playerState_t, hud) - after_command_time);
    printf("commandTime %d\n", ps->commandTime);
    for (int i = 0; i < snap->numEntities; ++i) {
        const entityState_t *ent = &active.parseEntities[(snap->parseEntitiesNum + i) & 2047];
        printf("entity %d", ent->number);
        print_words((const byte *)ent + sizeof ent->number, sizeof *ent - sizeof ent->number);
    }
    for (int i = 0; i < snap->numClients; ++i) {
        const clientState_t *client = &active.parseClients[(snap->parseClientsNum + i) & 2047];
        printf("clientstate %d", client->clientIndex);
        print_words((const byte *)client + sizeof client->clientIndex, sizeof *client - sizeof client->clientIndex);
    }
    const int current_count = sizeof ps->hud.current / sizeof ps->hud.current[0];
    const int archival_count = sizeof ps->hud.archival / sizeof ps->hud.archival[0];
    print_hud(ps->hud.current, current_count, 0);
    print_hud(ps->hud.archival, archival_count, current_count);
}

int main(int argc, char **argv)
{
    int port, seconds, last_snapshot = 0;
    char extra;
    if ((argc != 4 && argc != 5) ||
        sscanf(argv[1], "%d%c", &port, &extra) != 1 || port < 1024 || port > 65535 ||
        sscanf(argv[2], "%d%c", &seconds, &extra) != 1 || seconds < 1 || seconds > 120 ||
        (argc == 5 && (sscanf(argv[4], "%d%c", &last_snapshot, &extra) != 1 || last_snapshot < 1))) {
        fprintf(stderr, "Usage: %s <loopback-port> <seconds> <new-datagrams-file> [last-server-time]\n", argv[0]);
        return 2;
    }
    const struct sockaddr_in address = {.sin_family = AF_INET, .sin_port = htons(port),
                                        .sin_addr.s_addr = htonl(INADDR_LOOPBACK)};
    const struct timeval retry = {.tv_usec = 100000};
    udp = socket(AF_INET, SOCK_DGRAM, 0);
    if (udp < 0 || connect(udp, (const struct sockaddr *)&address, sizeof address) ||
        setsockopt(udp, SOL_SOCKET, SO_RCVTIMEO, &retry, sizeof retry))
        Com_Error(0, "UDP initialization: %s", strerror(errno));
    datagrams = fopen(argv[3], "wbx");
    if (!datagrams)
        Com_Error(0, "Cannot create %s: %s", argv[3], strerror(errno));
    conn.serverAddress = (netadr_t){ .type = NA_IP, .ip = {127, 0, 0, 1}, .port = htons(port) };
    Netchan_Setup(NS_CLIENT1, &conn.netchan, conn.serverAddress, 19073);
    conn.state = CA_CONNECTING;
    int started = Sys_Milliseconds(), last_request = -500;
    int snapshots = 0;
    byte data[65536];
    while ((cls.realtime = Sys_Milliseconds() - started) < seconds * 1000) {
        if (cls.realtime - last_request >= 300) {
            last_request = cls.realtime;
            if (conn.state == CA_CONNECTING) {
                send_oob("getchallenge");
            } else if (conn.state == CA_CHALLENGING) {
                char request[256];
                snprintf(request, sizeof request, "connect \"\\protocol\\118\\challenge\\%d\\qport\\19073\\name\\oracle\\rate\\25000\\snaps\\20\"", conn.challenge);
                send_oob(request);
            } else if (conn.state >= CA_PRIMED) {
                send_command();
            } else if (conn.serverMessageSequence > 0) {
                CL_WritePacket();
            }
        }
        ssize_t size = recv(udp, data, sizeof data - 1, 0);
        if (size < 0) {
            if (errno == EAGAIN || errno == ECONNREFUSED || errno == EINTR)
                continue;
            Com_Error(0, "recv: %s", strerror(errno));
        }
        uint32_t length = (uint32_t)size;
        if (fwrite(&length, sizeof length, 1, datagrams) != 1 || fwrite(data, 1, length, datagrams) != length)
            Com_Error(0, "Datagram write failed");
        if (size < 4)
            continue;
        if (!memcmp(data, "\xff\xff\xff\xff", 4)) {
            data[size] = 0;
            printf("oob %s\n", data + 4);
            if (conn.state == CA_CONNECTING && sscanf((char *)data + 4, "challengeResponse %d", &conn.challenge) == 1)
                conn.state = CA_CHALLENGING;
            else if (conn.state == CA_CHALLENGING && !strncmp((char *)data + 4, "connectResponse", 15))
                conn.state = CA_CONNECTED;
            last_request = -500;
            continue;
        }
        if (conn.state < CA_CONNECTED)
            continue;
        if (size > MAX_MSGLEN)
            Com_Error(0, "In-band datagram exceeds engine message capacity");
        msg_t message;
        MSG_Init(&message, data, MAX_MSGLEN);
        message.cursize = (int)size;
        if (!Netchan_Process(&conn.netchan, &message))
            continue;
        conn.serverMessageSequence = conn.netchan.incomingSequence;
        conn.reliableAcknowledge = MSG_ReadLong(&message);
        if (message.overflowed || conn.reliableAcknowledge < 0 || conn.reliableAcknowledge > conn.reliableSequence)
            Com_Error(0, "Invalid reliable acknowledgement");
        CL_Netchan_Decode(message.data + message.readcount, message.cursize - message.readcount);
        active.newSnapshots = 0;
        const int initial = conn.state == CA_CONNECTED;
        const int previous_command = conn.serverCommandSequence;
        printf("message %d %d\n", conn.serverMessageSequence, message.cursize);
        CL_ParseServerMessage(&message);
        if (initial && conn.state == CA_PRIMED) {
            printf("client %d\n", conn.clientNum);
            for (int i = 0; i < (int)(sizeof active.gameState.stringOffsets / sizeof active.gameState.stringOffsets[0]); ++i)
                if (active.gameState.stringOffsets[i])
                    printf("config %d %s\n", i, active.gameState.stringData + active.gameState.stringOffsets[i]);
        }
        for (int i = previous_command + 1; i <= conn.serverCommandSequence; ++i)
            printf("command %d %s\n", i, conn.serverCommands[i & 127]);
        if (active.newSnapshots) {
            ++snapshots;
            print_snapshot((int)size);
            if (last_snapshot && active.snap.serverTime >= last_snapshot)
                break;
        }
        if (conn.state >= CA_PRIMED) {
            send_command();
            last_request = cls.realtime;
        } else {
            /* Let the server complete its first frame after connect before
             * asking for the gamestate and the queued welcome commands. */
            CL_WritePacket();
            last_request = cls.realtime;
        }
    }
    if (fclose(datagrams) || !snapshots || (last_snapshot && active.snap.serverTime != last_snapshot))
        Com_Error(0, "No complete snapshots recorded or capture close failed");
    printf("received snapshots %d\n", snapshots);
    return 0;
}
