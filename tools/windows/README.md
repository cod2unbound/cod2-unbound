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
tools/windows/check-paths.sh "/path/to/CoD2"
```

On Apple silicon, allow about 10–15 minutes for a clean matrix and 2–3 minutes
for the headless checks. Incremental matrix runs reuse the build directories.
Run `tools/windows/check-servers.sh "/path/to/CoD2"` before every release
candidate and after any change under `src/`. Build the server binaries from
that checkout with the matrix first.

**CI.** The CI workflow runs the data-free Windows checks on pushes and pull
requests that touch the engine, CMake, Windows tools, export tools, platform
guard checker or workflow. It caches the MinGW image built from the pinned
Ubuntu 24.04 image. From a normal clone, run
`tools/windows/build-matrix.sh --source-checks` for the platform guards and
both source exports. CI uses public build dependencies and needs no game data
or secrets.

**Build matrix.** The script builds x64 Original 1.3 and CoD2x dedicated, Direct3D 9,
and diagnostic OpenGL binaries, plus the i686 Original 1.3 Direct3D 9 probe.
The first build downloads checksummed SDL2 2.30.9 and curl 8.14.1, and builds static
libcurl once per architecture. Set `COD2_WINDOWS_JOBS` to change Ninja's parallelism
from the default of three. Both scripts accept a locally built image name via
`COD2_WINDOWS_IMAGE`. Use `COD2_WINDOWS_OUTPUT` to choose a different absolute evidence directory.

Each target directory under `output/r05/builds/matrix/` contains the executable,
`configure.log`, `build.log`, `pe-header.txt` and `imports.txt`. The build checks
the PE architecture and looks for the system Direct3D 9 entry point on the
selected renderer path. It checks that CMake rejects the obsolete
`COD2_WIN32_REAL_D3D9` option and names its replacement. It fails on a DLL
import outside [`pe-imports/`](pe-imports/). Review the reason for a new dependency before
updating its list. Direct3D 9 and its shader compiler are loaded dynamically.

**Headless Wine checks.** Pass a clean licensed game folder containing `main/*.iwd`.
Omit the path to use `~/Games/CoD2`. The check mounts those files read-only, boots
a fresh Wine prefix and private home for each server start in a container with
loopback networking only. Every start sets `developer 0` and points
the three `COD2X_TEST_*` service settings at `127.0.0.1:9`. The command writes
`receipt.txt` under `output/r05/builds/wine/headless-<UTC date and time>-*/`,
alongside the logs and traces. It records the fixture revision, image ID,
server binary hashes and both results. The temporary prefixes are removed
after use.

Each edition loads `mp_toujane` in DM, answers local `getinfo` and `getstatus`
queries, and then replays the same two synthetic clients for 600 simulation
ticks. `tools/parity/compare.py --raw --require-clients 2` checks the result
against the saved Windows x64 reference traces in
[`fixtures/two-bot/`](fixtures/two-bot/README.md). These traces cover the
tracked entity hashes and player fields for this fixture.

**Path and credential checks.** `check-paths.sh` runs disposable Steam, retail, portable,
missing-data, Unicode and extended-path fixtures. It checks the selected source, per-user
writes, unchanged licensed and VirtualStore files, and Credential Manager precedence with
synthetic keys. See [the Windows path contract](../../docs/windows-game-data.md).

**Retail 1.3 comparison.** Set `COD2_WINDOWS_RETAIL_DIR` to the directory
containing the licensed 1.3 `CoD2MP_s.exe` and renderer DLL to compare a
fresh retail replay with the Original 1.3 server. The licensed game
directory supplies `main/*.iwd` and `mss32.dll`.

```sh
COD2_WINDOWS_RETAIL_DIR="/path/to/Windows 1.3" tools/windows/check-servers.sh "/path/to/CoD2"
```

The recorder checks the executable's SHA-256 and builds a Miles DLL forwarder
from the local exports. It runs the original PE with the pinned i386 Wine 8.0
image and a private Xvfb display in a network-isolated container. Set
`COD2_WINDOWS_RETAIL_IMAGE` to the ID of a compatible local i386 Wine image
with Xvfb. The comparison checks raw entity-state hashes and the tracked player
fields for every tick. The capture and comparison result are written under
`output/r05/oracle/`. The command exits nonzero if the replay differs.

For the short four-client retail cases on additional stock maps, pass the same
game and retail paths to `tools/windows/check-retail-cases.sh`. Its optional
third argument selects one case from `fixtures/retail-extra/cases.tsv`.

For an upstream CoD2x reference that loads the test-client scripts from an IWD,
pass `--pack-test-scripts` to `tools/parity/record.py` and use the same scripts in
the reference archive. Loading the two scripts as loose files consumes two
random values for file checksums and changes the later bullet spread.

For the protocol 118 loopback comparison, run `tools/windows/check-snapshot-wire.sh`
with the game path, retail path, successful Toujane retail capture directory,
and a new output directory. For an extra replay, pass a name from
`fixtures/retail-extra/cases.tsv` in place of the Toujane capture directory.
The wire check builds a fresh retail recorder and starts each server on private
loopback. It lets the test clients spawn, then connects one spectator using
the engine's client parser. The server-side test clients have no UDP receiver.

Both servers use a fixed 5 ms clock increment in an offline `devmap`, so
wall-clock stalls cannot combine multiple simulation ticks before a snapshot
send. The spectator waits for the first server message before asking for
gamestate, then sends its acknowledgement and command on receipt of each
snapshot. The check starts after the handshake and the first acknowledged
post-handshake snapshot. It requires every snapshot time and a matching
delta base on both sides. It checks the decoded player (including `commandTime`),
entity, HUD and client state, initial config strings, reliable commands,
message order and packet sizes. The receipt prints startup snapshot times
outside the fixed comparison window and the UDP payload byte counts.

## Direct3D 9

`-DCOD2_WIN32_D3D9=ON` makes the client create a real Direct3D 9 device on its SDL window, as
Windows 1.3 does, with no OpenGL context. It compiles the game's own HLSL from `main/` at material
load with `D3DCompile` from `d3dcompiler_47.dll`, which Windows 10 (22H2) and Windows 11 ship. The loader
also tries `d3dcompiler_46.dll` and `d3dcompiler_43.dll` when present.

Previous runtime checks covered these configurations.

- **An Intel integrated chip on Windows 10 (22H2):** both editions loaded Toujane,
  Harbor, Carentan and Burgundy and restarted the renderer in Direct3D 9 and DirectX 7
  modes, both directly and from the launcher. Windowed loopback matches also ran.
- **An AMD Radeon discrete chip on Windows 10 (22H2):** both editions rendered
  on the Radeon in Direct3D 9 and DirectX 7 modes in earlier windowed and fullscreen trials.
- **Windows 11 in a virtual machine:** Microsoft's `d3d9.dll` and shader compiler
  ran with software rendering. The menu, Toujane, `vid_restart`, a mode change and
  exclusive fullscreen ran, and the menu and map shaders compiled.
- **Wine with DXVK:** four maps in the default and Direct3D 7 paths. Wine's own HLSL compiler
  rejects a few of the game's shaders (which ones depends on the Wine version), so those runs used
  bytecode compiled from the same HLSL with vkd3d-shader. Expect missing materials under Wine
  without that.

Remaining physical checks for the release candidate include a Windows 11 x64 PC,
repeating the checks on the Radeon, exclusive fullscreen recovery after Alt+Tab,
Harbor water before and after a renderer restart in both modes and editions, and
startup and renderer restart on a selected second monitor in windowed and
exclusive modes.

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

## Online downloads and player data

A Windows client joining a modded server downloads the missing files and
restarts into the mod without asking, when Allow Downloading is set to Yes in
Multiplayer Options. This is what the original 1.3 does, and both editions
keep it; set the option to No to be warned instead
([issue #143](https://github.com/cod2unbound/cod2-unbound/issues/143)).

Both editions store writable files under `%LOCALAPPDATA%\CoD2 Unbound` by default.
Profiles live under `main\players` there. Configs, mods, downloads, demos,
screenshots and logs use the writable game home, which is separate from the
licensed installation. Launcher settings live in the `launcher` subdirectory.

Both editions prefer the explicit `CoD2 Unbound/CD Key` credential in Windows
Credential Manager. If that credential is absent, the first valid key from the
legacy VirtualStore entries or retail registry wins, in that order. The launcher
can copy a valid installed key into Credential Manager with the player's approval.
With `COD2_UNBOUND_TEST_KEY_SLOT` set to 1–32 lowercase ASCII letters, digits or
hyphens, both engines and the Fixture launcher use only
`CoD2 Unbound/CD Key (test <slot>)`. An empty slot gives an unset CD key. The
Fixture launcher and its game inherit the same slot. The Public launcher
uses its regular key store and passes its environment to the engine.
See the [Windows game data and user files contract](../../docs/windows-game-data.md)
for discovery, instance paths and the full key lookup order.

Original 1.3's safe-mode prompt after an unclean direct start and its
handling of a second copy still need work. The Windows port uses IP
networking.

## Sound

Windows uses the same mixer as macOS, `src/platform/macos_audio.c`, with SDL playing the output and
reading WAV, and `third_party/minimp3` reading MP3. The original loads `mss32.dll`, which is
closed-source and 32-bit only.

Stock multiplayer maps can log `ERROR: Sound file 'sound/misc/beep.wav' not found`
when audio initializes. The stock `beep` alias in `soundaliases/iw_multiplayer2.csv`
points to a missing asset. Retail Windows 1.3 logs the same warning when loading
Harbor with audio enabled.

The x64 Original 1.3 client uses SDL for voice capture and the shared Speex and playback paths.
It opens the default microphone when you use push-to-talk. The CoD2x client follows the upstream
release with voice chat disabled. The i686 matrix probe uses a stub for voice.

To check sound with no audio hardware, send it to a file with `SDL_AUDIODRIVER=disk` and
`SDL_DISKAUDIOFILE=sound.raw`. The file contains 32-bit float PCM at the mix rate selected by
`snd_khz`, with mono or stereo selected by `snd_stereo`. The defaults are 44.1 kHz and stereo.

## Demo playback

`MAX_PACKET_USERCMDS` can appear as playback returns to the menu. The client still
builds input commands during playback. At disconnect the stock send path can see
more than 32 commands since its last packet and logs that it capped the batch.
Retail 1.3 has the same disconnect and packet limit.

## The older plain path

`-DCOD2_WIN32_LEGACY=ON` builds the non-`COD2_X64` branches with the 32-bit data blobs. It links and
boots, but compiles stale logic: collision, menus, HUD and more differ from Windows 1.3.

## Windows releases

The release targets x64 PCs running Windows 10 (22H2) or Windows 11.
Setup and the launcher use build 19041 as their minimum.

From a clean checkout, run `COD2_WINDOWS_COMPANY='Publisher name' tools/windows/release.sh layout`.
This stages both dedicated servers and both system Direct3D 9 clients under the
printed release directory's `layout`. The binaries retain the DirectX 7
renderer preference. The script uses the local release build image by content
ID. `COD2_WINDOWS_JOBS` defaults to 3. Read
[Windows release and update notes](../../docs/windows-release.md) for the
headless compiler, test and production signing modes, and an offline Wine
install check.
