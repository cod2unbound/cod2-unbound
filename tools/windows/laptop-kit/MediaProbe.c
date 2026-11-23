#define COBJMACROS
#include <windows.h>
#include <initguid.h>
#include <d3d9.h>
#include <dxgi.h>
#include <d3dcompiler.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void adapter_memory(void)
{
    IDXGIFactory1 *factory;
    HRESULT created = CreateDXGIFactory1(&IID_IDXGIFactory1, (void **)&factory);
    if (FAILED(created)) {
        printf("DXGI adapter inventory unavailable (0x%08lx)\n", (unsigned long)created);
        return;
    }
    for (UINT index = 0; ; index++) {
        IDXGIAdapter1 *adapter;
        if (FAILED(IDXGIFactory1_EnumAdapters1(factory, index, &adapter))) break;
        DXGI_ADAPTER_DESC1 info;
        if (SUCCEEDED(IDXGIAdapter1_GetDesc1(adapter, &info))) {
            char name[512] = "";
            WideCharToMultiByte(CP_UTF8, 0, info.Description, -1, name, sizeof(name), NULL, NULL);
            printf("DXGI adapter %u: %s | dedicated video bytes=%llu | shared bytes=%llu | software=%u\n",
                   index, name, (unsigned long long)info.DedicatedVideoMemory,
                   (unsigned long long)info.SharedSystemMemory, !!(info.Flags & DXGI_ADAPTER_FLAG_SOFTWARE));
        }
        IDXGIAdapter1_Release(adapter);
    }
    IDXGIFactory1_Release(factory);
}

static int adapter(void)
{
    adapter_memory();
    IDirect3D9 *d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) {
        puts("Direct3DCreate9 returned NULL");
        return 1;
    }
    UINT count = IDirect3D9_GetAdapterCount(d3d);
    printf("Direct3D9 adapter count %u\n", count);
    for (UINT i = 0; i < count; i++) {
        D3DADAPTER_IDENTIFIER9 id;
        D3DDISPLAYMODE mode;
        HRESULT hr = IDirect3D9_GetAdapterIdentifier(d3d, i, 0, &id);
        if (FAILED(hr)) {
            printf("GetAdapterIdentifier(%u) 0x%08lx\n", i, (unsigned long)hr);
            continue;
        }
        printf("Adapter %u: %s | %s | %s | %04lx:%04lx\n", i, id.Description, id.Driver, id.DeviceName,
               (unsigned long)id.VendorId, (unsigned long)id.DeviceId);
        if (SUCCEEDED(IDirect3D9_GetAdapterDisplayMode(d3d, i, &mode)))
            printf("Mode %u x %u at %u Hz, format %d\n", mode.Width, mode.Height, mode.RefreshRate, mode.Format);
    }
    IDirect3D9_Release(d3d);
    return count ? 0 : 1;
}

static void *read_bytes(const char *name, UINT *length)
{
    FILE *stream = fopen(name, "rb");
    if (!stream) return NULL;
    fseek(stream, 0, SEEK_END);
    long size = ftell(stream);
    rewind(stream);
    void *buffer = size > 0 ? malloc((size_t)size) : NULL;
    if (buffer && fread(buffer, 1, (size_t)size, stream) != (size_t)size) {
        free(buffer);
        buffer = NULL;
    }
    fclose(stream);
    *length = buffer ? (UINT)size : 0;
    return buffer;
}

static HRESULT WINAPI include_open(ID3DInclude *self, D3D_INCLUDE_TYPE type, const char *name,
                                   const void *parent, const void **data, UINT *length)
{
    (void)self;
    (void)type;
    (void)parent;
    *data = read_bytes(name, length);
    if (!*data) {
        char path[512];
        snprintf(path, sizeof(path), "lib/%s", name);
        *data = read_bytes(path, length);
    }
    return *data ? S_OK : E_FAIL;
}

static HRESULT WINAPI include_close(ID3DInclude *self, const void *data)
{
    (void)self;
    free((void *)data);
    return S_OK;
}

static int shaders(void)
{
    const D3D_SHADER_MACRO macros[] = {{"PC", ""}, {"IW_HLSL", "1"}, {NULL, NULL}};
    ID3DIncludeVtbl include_methods = {include_open, include_close};
    ID3DInclude include = {&include_methods};
    FILE *list = fopen("shader-requests.tsv", "r");
    if (!list) return 2;
    char name[256], entry[32], target[32];
    int failed = 0;
    puts("shader\tentry\tprofile\tresult\tbytecode-bytes\tcompiler-messages");
    while (fscanf(list, "%255s %31s %31s", name, entry, target) == 3) {
        UINT length = 0;
        void *source = read_bytes(name, &length);
        ID3DBlob *code = NULL;
        ID3DBlob *message = NULL;
        HRESULT hr = source ? D3DCompile(source, length, name, macros, &include, entry, target,
            D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY, 0, &code, &message) : E_FAIL;
        printf("%s\t%s\t%s\t0x%08lx\t%llu\t", name, entry, target,
            (unsigned long)hr, code ? (unsigned long long)ID3D10Blob_GetBufferSize(code) : 0ULL);
        if (!source) fputs("source missing", stdout);
        if (message) {
            const char *m = ID3D10Blob_GetBufferPointer(message);
            for (; *m; ++m) {
                if (*m == '\r') continue;
                if (*m == '\n') fputs(" | ", stdout);
                else putchar(*m == '\t' ? ' ' : *m);
            }
            ID3D10Blob_Release(message);
        }
        putchar('\n');
        if (code) ID3D10Blob_Release(code);
        free(source);
        failed += FAILED(hr);
    }
    fclose(list);
    return failed ? 1 : 0;
}

static void chunk(FILE *file, const char *label, DWORD size)
{
    fwrite(label, 1, 4, file);
    fwrite(&size, sizeof(size), 1, file);
}

static double block_energy(const BYTE *data, DWORD length, const WAVEFORMATEX *format)
{
    WORD subtype = format->wFormatTag;
    if (subtype == WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22)
        subtype = (WORD)((const WAVEFORMATEXTENSIBLE *)format)->SubFormat.Data1;
    unsigned stride = format->wBitsPerSample / 8;
    double energy = 0;
    for (DWORD i = 0; stride && i + stride <= length; i += stride) {
        double sample;
        if (subtype == WAVE_FORMAT_IEEE_FLOAT && stride == 4) {
            float raw;
            memcpy(&raw, data + i, sizeof(raw));
            sample = raw;
        } else if (subtype == WAVE_FORMAT_PCM && stride == 2) {
            int16_t raw;
            memcpy(&raw, data + i, sizeof(raw));
            sample = raw / 32768.0;
        } else if (subtype == WAVE_FORMAT_PCM && stride == 4) {
            int32_t raw;
            memcpy(&raw, data + i, sizeof(raw));
            sample = raw / 2147483648.0;
        } else {
            return -1;
        }
        energy += sample * sample;
    }
    return energy;
}

#define CHECK(call) do { hr = (call); if (FAILED(hr)) { fprintf(stderr, "%s: 0x%08lx\n", #call, (unsigned long)hr); goto end; } } while (0)
static int audio(const char *path, int seconds)
{
    HRESULT hr;
    IMMDeviceEnumerator *enumerator = NULL;
    IMMDevice *device = NULL;
    IAudioClient *client = NULL;
    IAudioCaptureClient *capture = NULL;
    WAVEFORMATEX *format = NULL;
    FILE *file = NULL;
    DWORD bytes = 0;
    DWORD packets = 0;
    DWORD active = 0;
    double energy = 0;
    long data_length = 0;
    int initialized = 0, started = 0, result = 1;
    CHECK(CoInitializeEx(NULL, COINIT_MULTITHREADED));
    initialized = 1;
    CHECK(CoCreateInstance(&CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL,
                           &IID_IMMDeviceEnumerator, (void **)&enumerator));
    CHECK(IMMDeviceEnumerator_GetDefaultAudioEndpoint(enumerator, eRender, eConsole, &device));
    CHECK(IMMDevice_Activate(device, &IID_IAudioClient, CLSCTX_ALL, NULL, (void **)&client));
    CHECK(IAudioClient_GetMixFormat(client, &format));
    CHECK(IAudioClient_Initialize(client, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                                  10000000, 0, format, NULL));
    CHECK(IAudioClient_GetService(client, &IID_IAudioCaptureClient, (void **)&capture));
    file = fopen(path, "wb");
    if (!file) goto end;
    const DWORD fmt_size = (DWORD)sizeof(*format) + format->cbSize;
    chunk(file, "RIFF", 0);
    fwrite("WAVE", 1, 4, file);
    chunk(file, "fmt ", fmt_size);
    fwrite(format, 1, fmt_size, file);
    if (fmt_size & 1) fputc(0, file);
    data_length = ftell(file) + 4;
    chunk(file, "data", 0);
    CHECK(IAudioClient_Start(client));
    started = 1;
    printf("Opened shared WASAPI render loopback, %u Hz, %u channels, %u bits\n",
           (unsigned)format->nSamplesPerSec, (unsigned)format->nChannels, (unsigned)format->wBitsPerSample);
    fflush(stdout);
    ULONGLONG deadline = GetTickCount64() + (ULONGLONG)seconds * 1000;
    BYTE zeros[256] = {0};
    while (GetTickCount64() < deadline) {
        UINT32 ready;
        CHECK(IAudioCaptureClient_GetNextPacketSize(capture, &ready));
        while (ready) {
            BYTE *data;
            UINT32 frames;
            DWORD flags;
            CHECK(IAudioCaptureClient_GetBuffer(capture, &data, &frames, &flags, NULL, NULL));
            DWORD length = frames * format->nBlockAlign;
            bytes += length;
            packets++;
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || !data) {
                while (length > sizeof(zeros)) {
                    fwrite(zeros, 1, sizeof(zeros), file);
                    length -= sizeof(zeros);
                }
                fwrite(zeros, 1, length, file);
            } else {
                fwrite(data, 1, length, file);
                active++;
                double block = block_energy(data, length, format);
                if (energy >= 0) energy = block < 0 ? -1 : energy + block;
            }
            CHECK(IAudioCaptureClient_ReleaseBuffer(capture, frames));
            CHECK(IAudioCaptureClient_GetNextPacketSize(capture, &ready));
        }
        Sleep(25);
    }
    printf("Capture bytes %lu, packets %lu, non-silent flags %lu\n",
           (unsigned long)bytes, (unsigned long)packets, (unsigned long)active);
    if (bytes && energy >= 0) printf("RMS=%.9f\n", sqrt(energy * (format->wBitsPerSample / 8) / bytes));
    result = bytes ? 0 : 3;
end:
    if (file) {
        DWORD riff_length = (DWORD)ftell(file) - 8;
        fseek(file, 4, SEEK_SET);
        fwrite(&riff_length, sizeof(riff_length), 1, file);
        fseek(file, data_length, SEEK_SET);
        fwrite(&bytes, sizeof(bytes), 1, file);
        fclose(file);
    }
    if (started) IAudioClient_Stop(client);
    if (capture) IAudioCaptureClient_Release(capture);
    if (format) CoTaskMemFree(format);
    if (client) IAudioClient_Release(client);
    if (device) IMMDevice_Release(device);
    if (enumerator) IMMDeviceEnumerator_Release(enumerator);
    if (initialized) CoUninitialize();
    return result;
}

int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "adapter")) return adapter();
    if (argc == 2 && !strcmp(argv[1], "shaders")) return shaders();
    if (argc == 4 && !strcmp(argv[1], "audio")) {
        int duration = atoi(argv[3]);
        if (duration > 0 && duration <= 40) return audio(argv[2], duration);
    }
    return 2;
}
