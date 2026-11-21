# CoD2 Unbound launcher core

The solution contains a platform-neutral class library and a headless parity test
executable. The Windows launcher and a future Linux launcher can reference
`CoD2Unbound.Core` directly.

## Build and test

On Linux or macOS with Linux Docker containers, run from the repository root:

```sh
sh dotnet/test.sh
```

On Windows, run the same command in WSL with Docker integration and Linux containers.
On Windows, Linux or macOS with a local .NET 10 SDK, you can also run these commands
from the repository root in PowerShell or a POSIX shell:

```text
dotnet build dotnet/CoD2Unbound.sln -c Release -m:4
dotnet run --project dotnet/CoD2Unbound.Core.Tests -c Release --no-build
```

The Docker runner builds and executes with networking disabled. On first use Docker
fetches the pinned Microsoft SDK image. The build and test method has no NuGet package
dependencies and uses a test executable with explicit assertions and a failing exit
status. Both projects enable nullable checks, analyzers, warnings as errors and
deterministic compilation. The catalogue reader uses source-generated JSON metadata.
The existing bundled settings and commands catalogue is embedded directly from
`launcher/Resources/ConfigCatalog.json`.

## Toolchain checked on 2026-11-21

- .NET 10 is Microsoft's current LTS. The pinned multi-architecture Docker image used
  SDK 10.0.404 and runtime 10.0.15. [Support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- Windows App SDK 2.7.0 is the current stable release. Microsoft's WinUI 3 C#
  quickstart uses the .NET 10 SDK for the command-line workflow or Visual Studio 2026
  with the WinUI application development workload. The core targets `net10.0` so
  the future Windows project can reference it from a Windows-targeted .NET 10 app.
  [Windows App SDK release channels](https://learn.microsoft.com/windows/apps/windows-app-sdk/release-channels)
  [WinUI quickstart](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/create-your-first-winui3-app)
  [Windows App SDK package 2.7.0](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.7.0)

## Swift reference fixtures

Both suites read `tests/launcher/fixtures/core-parity/`. On macOS with Xcode,
`sh tests/launcher/core_parity.sh --check` recompiles the Swift reference code and
compares its answers with the checked-in JSON. To refresh expectations after a
deliberate Swift change, run `sh tests/launcher/core_parity.sh --write` against
that revision and review the diff before committing. Every fixture records the
Swift source revision. Binary inputs and outputs use Base64, and config ranges use
zero-based, half-open byte offsets.

## Windows adapter responsibilities

See [the port map](PORT-MAP.md) for the Swift counterparts and remaining core
work. Windows adapters should supply the following services.

| Core service | Windows adapter |
| --- | --- |
| `ISettingsFolder` | Resolve a private per-user settings root. Restrict reads and writes to that root, refuse reparse-point traversal, and publish settings with atomic replacement. |
| `ISecretStore` | Use user-scoped Credential Manager entries for the CD key and remembered server passwords. Route a key to the engine through its Windows key contract and keep credentials out of logs and process arguments. |
| `IGameFiles` | Resolve validated relative paths under the selected engine home and verify regular files without traversing junctions or symlinks. |
| `IGameDiscovery` | Read the installer inventory for client and dedicated executables by edition. Discover licensed retail data and Steam libraries using Windows registry views and Steam's library records, then validate the archive set. |
| `IProcessSupervisor` | Use `CreateProcessW` with a fully qualified executable as `lpApplicationName`, and pass `WindowsCommandLine.Format` as the raw command line. Check the ANSI engine can represent its inputs. Use job objects for owned children, observe externally started games without taking ownership, and drain and redact stdout and stderr. |
| `IEngineProcess` | Expose the owned process ID, completion and exit status. Attempt graceful shutdown and use the job for forced cleanup when required. |
| `IUdpTransport` | Use connected UDP, bound DNS and setup separately, start the response timer at send, and retain valid packets already received on timeout. Stop at the requested packet count and honor cancellation. |
| `IExternalLinks` | Open validated external destinations with the Windows shell. |
| `IFileAssociations` | Deliver ordered launch and forwarded activations for `.cfg`, `.dm_1`, and `cod2x://`. The installer owns registration. Coordinate the protocol handler with the CoD2x engine's existing self-registration in `Win_Cod2xRegisterLinks`. |

The Windows build passes raw command text to `WinMain`. The planner quotes
the executable and preserves the engine's own double quotes in the tail.
It normalizes Windows path separators in engine values so a trailing backslash
cannot escape the closing engine quote. The process adapter publishes the
generated join config with a user-private ACL before spawning. It removes the
file on a failed start and after use, with recovery for interrupted launcher
sessions. Join config contents may include a password. Engine output and crash
capture need redaction. [CreateProcessW](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)

The release offer takes an expected archive name from its caller. Windows
packaging defines that name and verifies the installer publisher or server ZIP
signature before deployment. The core checks release metadata and SHA-256.
