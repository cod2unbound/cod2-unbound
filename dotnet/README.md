# CoD2 Unbound launcher core

The solution contains a platform-neutral class library and a headless parity test
executable. The Windows launcher and a future Linux launcher can reference
`CoD2Unbound.Core` directly.

## Build and test

On Linux or macOS with Linux Docker containers, run from the repository root:

```sh
sh dotnet/fetch-packages.sh output/dotnet/nuget
sh dotnet/test.sh output/dotnet/nuget
```

On Windows, run the same command in WSL with Docker integration and Linux containers.
On Windows, Linux or macOS with a local .NET 10 SDK, you can also run these commands
from the repository root in PowerShell or a POSIX shell:

```text
dotnet restore dotnet/CoD2Unbound.sln --locked-mode --source output/dotnet/nuget -p:NuGetAudit=false
dotnet build dotnet/CoD2Unbound.sln --no-restore -c Release -m:4
dotnet run --project dotnet/CoD2Unbound.Core.Tests -c Release --no-build
```

The Docker runner mounts the source and local NuGet feed read-only, restores in
locked mode, and builds and tests with networking disabled. Populate the local feed
once with the separate networked acquisition command. It checks the package's
pinned SHA-256 and verifies its NuGet signatures before publishing it. The core
has one direct dependency, `BouncyCastle.Cryptography` 2.7.0. .NET 10 lacks a
standalone Ed25519 verification API. Bouncy Castle supplies a managed verifier for
Windows and Linux with zero transitive packages. Both lock files record its content
hash and no package files are tracked. The test executable uses explicit assertions
and a failing exit status. [Ed25519 API proposal](https://github.com/dotnet/runtime/issues/63174)
[Package 2.7.0](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0)
[Locked restore](https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files#locking-dependencies) Both projects enable nullable checks, analyzers, warnings as errors and
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
| `IConfigDirectoryStorage` | Use a user-private `FOLDERID_LocalAppData` subdirectory. Hold a cross-process `LockFileEx` lease across read, compare, discard and publish. Check every path component and final handle using `CreateFileW` with `FILE_FLAG_OPEN_REPARSE_POINT` and handle metadata. Refuse reparse points, directories in file slots and non-private paths. Stage writes on the same volume, flush them and publish with `ReplaceFileW` or a same-directory move for a new file. Test junction and symlink substitution, competing refreshes, process termination during publish, ACLs and corrupt-cache recovery as a standard user on NTFS. |
| `IConfigTextFetcher` | Use a .NET 10 `HttpClient` with manual redirect validation, response headers first and bounded streaming. Disable cookies and ambient credentials. Preserve response identity and classify cancellation, policy failures and connectivity separately. Test exact-size and oversized declared/chunked bodies, refused redirects, cancellation, timeout, and cache fallback on a Windows PC against a local stand-in. |
| `IConfigImportFiles` | Resolve the selected file or folder, then read regular files relative to that root with bounded streams. Use `CreateFileW` and handle metadata to refuse reparse-point escapes, alternate data streams and Win32 device names, including paths that change during validation. Test file selection, nested files, substituted junctions and symlinks, oversized files, inaccessible files and an NTFS root with spaces and Unicode. |
| `IConfigImportStore` | Hold the launcher session's draft and engine ownership interlocks and the profile store lease during version comparison and transactional commit. Preserve `active.txt` and return the receipt of an already-published import. Test two launcher processes, a game save racing an import, an unsaved draft, and a failed write followed by retry. |
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

The catalogue accepts only the trusted index and signature responses and
verifies the original index bytes. Its profile fetch passes the signed bytes through the import loader and retains
provenance. Dependencies referenced by a signed profile remain unresolved.
Construct a `ConfigImportSession` with the setup's source platform and the launcher's
destination platform. Windows uses `ConfigImportPlatform.Windows` for its destination.
The session re-reviews the composed config, prevents overlapping applies, and
hands one compare-and-commit request to its store. The stored trust fields identify the cache and the
configured public key provides authority. A network outage can use a reverified
copy. A valid response may repeat a revision only if the original bytes match.
[Reparse points](https://learn.microsoft.com/windows/win32/fileio/reparse-points-and-file-operations)
[LockFileEx](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-lockfileex)
[ReplaceFileW](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew)

The release offer takes an expected archive name from its caller. Windows
packaging defines that name and verifies the installer publisher or server ZIP
signature before deployment. The core checks release metadata and SHA-256.
