# Building for Windows

MinGW builds the reconciled engine for Windows from `cmake/windows-native.cmake`.
The Windows release targets x86_64. The i686 build is a link and import probe.

| Output | Option | What it is |
| --- | --- | --- |
| `cod2_win32_ded.exe` | default | Dedicated server |
| `cod2_win32.exe` | `-DCOD2_WIN32_CLIENT=ON` | SDL2/OpenGL diagnostic client |
| `cod2_win32.exe` | `-DCOD2_WIN32_CLIENT=ON -DCOD2_WIN32_D3D9=ON` | Client that renders through the system `d3d9.dll` |

Add `-DCOD2_FEATURE_CFLAGS=-DCOD2_CODX=1` for CoD2x. `COD2_WIN32_D3D9` is the system
Direct3D 9 option for the native MinGW client and defaults to OFF.

## Local build and server checks

Run from the repository root with Docker running. Build the tools image once.

```sh
docker build --platform linux/amd64 -t cod2-build-win tools/windows
tools/windows/build-matrix.sh
tools/windows/check-servers.sh "/path/to/CoD2"
```

On Apple silicon, allow about 10–15 minutes for a clean matrix and 2–3 minutes
for the headless checks. Incremental matrix runs reuse the build directories.

**Build matrix.** The script builds x64 Original 1.3 and CoD2x dedicated, Direct3D 9,
and diagnostic OpenGL binaries, plus the i686 Original 1.3 Direct3D 9 probe.
The first build downloads checksummed SDL2 2.30.9 and curl 8.14.1, and builds static
libcurl once per architecture. Set `COD2_WINDOWS_JOBS` to change Ninja's parallelism
from the default of three. Both scripts accept a locally built image name via
`COD2_WINDOWS_IMAGE`.

Each target directory under `output/r05/builds/matrix/` contains the executable,
`configure.log`, `build.log`, `pe-header.txt` and `imports.txt`. The build checks
the PE architecture and looks for the system Direct3D 9 entry point on the
selected renderer path. It fails on a DLL import outside
[`pe-imports/`](pe-imports/). Review the reason for a new dependency before
updating its list. Direct3D 9 and its shader compiler are loaded dynamically.

**Headless Wine checks.** Pass a clean licensed game folder containing `main/*.iwd`.
Omit the path to use `~/Games/CoD2`. The check mounts those files read-only, boots
a fresh Wine prefix and private home for each server start in a container with
loopback networking only. Every start sets `developer 0` and points
the three `COD2X_TEST_*` service settings at `127.0.0.1:9`. Logs and traces stay in
a new directory under `output/r05/builds/wine/`. The temporary prefixes are
removed after use.

Each edition loads `mp_toujane` in DM, answers local `getinfo` and `getstatus`
queries, and then replays the same two synthetic clients for 600 simulation
ticks. `tools/parity/compare.py --raw --require-clients 2` checks the result
against the saved Windows x64 reference traces in
[`fixtures/two-bot/`](fixtures/two-bot/README.md). These traces cover the
tracked entity hashes and player fields for this fixture. Retail parity and
rendering require separate acceptance checks.

## Direct3D 9

`-DCOD2_WIN32_D3D9=ON` makes the client create a real Direct3D 9 device on its SDL window, as
Windows 1.3 does, with no OpenGL context. It compiles the game's own HLSL from `main/` at material
load with `D3DCompile` from `d3dcompiler_47.dll` (then `_46`, `_43`), which Windows 10 and 11 ship.

Previous x86_64 runtime checks covered these configurations.

- **Windows 11 in a virtual machine**, with Microsoft's `d3d9.dll` and compiler and software
  rendering: the menu, a map, `vid_restart`, a mode change and exclusive fullscreen. Every shader
  the menu and `mp_toujane` need compiled. No machine with a graphics card has run it.
- **Wine with DXVK:** four maps in the default and Direct3D 7 paths. Wine's own HLSL compiler
  rejects a few of the game's shaders (which ones depends on the Wine version), so those runs used
  bytecode compiled from the same HLSL with vkd3d-shader. Expect missing materials under Wine
  without that.

### Previous Wine client diagnostics

To see the client without a monitor, also install `xvfb libgl1-mesa-dri libgl1-mesa-dri:i386
libgl1:i386 libglx-mesa0:i386` and the i386 X11 libraries in the container, start `Xvfb`, and set
`DISPLAY`, `LIBGL_ALWAYS_SOFTWARE=1` and `GALLIUM_DRIVER`. Under Rosetta, use `softpipe` on x86_64,
because `llvmpipe` draws a green sky there with the same engine state. Use `llvmpipe` on i686, because
`softpipe` got stuck in one draw call while loading a map and stayed there for the full 8-minute run.

On an arm64 Mac, `rosetta error: invalid gdt selector index 5` from an x86_64 build means the game
faulted, or was killed by `timeout`, and Rosetta hid the crash report. Reproduce on i686, whose crash
handler works there.

## Building on Windows (not tested)

The Docker checks above cover cross-compilation and headless Wine.

**MSYS2.** In the UCRT64 shell (MINGW32 for a 32-bit build, with the matching package prefix):

```sh
pacman -S --needed mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake \
  mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-zlib mingw-w64-ucrt-x86_64-python
cmake -S . -B out -G Ninja
cmake --build out
cmake -S . -B out-client -G Ninja -DCOD2_WIN32_CLIENT=ON -DCOD2_WIN32_D3D9=ON
cmake --build out-client
```

The toolchain files are for cross-compiling, so leave them out here. The client's configure step
needs `sh`, `curl`, `sha256sum` and `tar`, which an MSYS2 shell has.

**MSVC.** The `msvc-*` presets in `CMakePresets.json` are separate and unfinished. They do not build
this engine path: `cmake/win32-msvc.cmake` compiles the older plain path, and its x64 preset is
compile-only.

## What is missing

- **Downloading from a server** is in, but only half tested. The client links a static libcurl
  that configure builds from pinned, checksummed source (`tools/windows/fetch-curl.sh`), with
  Windows' own TLS. Under Wine the library fetched a file over HTTP and HTTPS and reported a
  missing one, using the calls the game makes. A download through the game itself needs a
  Windows install with a CD key to join a server, and was tested on macOS only.
- **The original launcher behaviour:** the safe-mode prompt after a crash, the second-copy check,
  the auto-update check and IPX.
- **The CD key** comes from the registry as on Windows 1.3 (the 32-bit view and the
  VirtualStore), then from the preferences file. **Preferences** use a file under `%HOME%`.
- **Voice chat.** The client is built without it (`src/win32/win_voice_none.c`): the DirectSound
  layer of Windows 1.3 is not in this source tree yet. A checkout that has one lists its files in
  `cmake/windows-voice.cmake` as `COD2_WIN_VOICE_C`, and the build uses them.

## Sound

Windows uses the same mixer as macOS, `src/platform/macos_audio.c`, with SDL playing the output and
reading WAV, and `third_party/minimp3` reading MP3. The original loads `mss32.dll`, which is
closed-source and 32-bit only.

To check sound with no audio hardware, send it to a file with `SDL_AUDIODRIVER=disk` and
`SDL_DISKAUDIOFILE=sound.raw`. The file is 44.1 kHz stereo 32-bit float.

## The older plain path

`-DCOD2_WIN32_LEGACY=ON` builds the non-`COD2_X64` branches with the 32-bit data blobs. It links and
boots, but compiles stale logic: collision, menus, HUD and more differ from Windows 1.3.
