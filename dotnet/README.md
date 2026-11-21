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

## Swift reference fixtures

Both suites read `tests/launcher/fixtures/core-parity/`. On macOS with Xcode,
`sh tests/launcher/core_parity.sh --check` recompiles the Swift reference code and
compares its answers with the checked-in JSON. To refresh expectations after a
deliberate Swift change, run `sh tests/launcher/core_parity.sh --write` against
that revision and review the diff before committing. Every fixture records the
Swift source revision. Binary inputs and outputs use Base64, and config ranges use
zero-based, half-open byte offsets.
