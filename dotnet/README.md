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
| `IConfigProfileFiles` | Scope paths to the private engine home. Hold `LockFileEx` on `main/players/.studio.lock`. Apply the same reparse-point and private-ACL rules as the directory cache. Use buffered writes followed by `FlushFileBuffers` and atomic publication. Move a completed staging directory within the same volume with `MoveFileExW`, without replacement. Test interrupted publishes of the journal, history, cfg, metadata and companion across real process restarts. Verify case-insensitive collisions, reserved filenames, read-only installs and concurrent game ownership. |
| `IConfigProfileTrash` | Use Shell `IFileOperation` with `FOFX_RECYCLEONDELETE` and capture the recycled item's identity from `PostDeleteItem`. Test Recycle Bin availability, aborted moves, exclusive restoration and restoring a profile's companion. Retain the profile if recovery cannot be guaranteed. |
| `IConfigImportStore` | Hold the launcher session's draft and engine ownership interlocks and the profile store lease during version comparison and transactional commit. Preserve `active.txt` and return the receipt of an already-published import. Test two launcher processes, a game save racing an import, an unsaved draft, and a failed write followed by retry. |
| `IConfigShareStorage` | Use the user-only settings folder and an exclusive `LockFileEx` lease for `shared-setups.json`. Keep the file across settings reset or profile Trash. Preserve withdrawal tokens across restart and upgrades, refuse unsafe paths, and omit tokens from logs and diagnostics. |
| `IConfigShareTransport` | Use the tested portable `HttpConfigShareTransport` with `SocketsHttpHandler`. On Windows, prove POST, validation, cancellation, whole-response deadlines and redirect refusal against a local service. |
| `IZpamPackageStorage` | Accept one private staging directory per validated release. Create regular files exclusively and publish the complete directory on the same volume. Preserve a previous package if publish fails, and remove only the unpublished stage on cancellation. Test interrupted replacement, access denied and reparse-point substitution on NTFS. |
| `ISecretStore` | Use user-scoped Credential Manager entries for the CD key and remembered server passwords. Route a key to the engine through its Windows key contract and keep credentials out of logs and process arguments. |
| `IGameFiles` | Resolve validated relative paths under the selected engine home and verify readable regular files without traversing junctions or symlinks. Check the sixteen licensed archives from both Steam and retail paths as a standard user. |
| `IGameDiscovery` | Read the installer inventory for client and dedicated executables by edition. Discover licensed retail data and Steam libraries using Windows registry views and Steam's library records, then validate the archive set. |
| `IProcessSupervisor` | Use `CreateProcessW` with a fully qualified executable as `lpApplicationName`, and pass `WindowsCommandLine.Format` as the raw command line. Check the ANSI engine can represent its inputs. Use job objects for owned children, observe externally started games without taking ownership, and drain and redact stdout and stderr. |
| `IEngineProcess` | Expose the owned process ID, completion and exit status. Attempt graceful shutdown and use the job for forced cleanup when required. |
| `IUdpTransport` | Use connected UDP, bound DNS and setup separately, start the response timer at send, and retain valid packets already received on timeout. Stop at the requested packet count and honor cancellation. |
| `IExternalLinks` | Open validated external destinations with the Windows shell. |
| `IFileAssociations` | Deliver ordered launch and forwarded activations for `.cfg`, `.dm_1`, and `cod2x://`. The installer owns registration. Coordinate the protocol handler with the CoD2x engine's existing self-registration in `Win_Cod2xRegisterLinks`. |
| `IGameSessionHost` and `IGameLaunchReservation` | Use the same per-user launch/install interlock for reservation, process creation and recovery. Check installed executable identity and creation time before observing, reconnecting to, returning to, or quitting a client. Pass the ordered identities to `ConfigStudioSession`. Keep intro state and privacy flags on the intended launch only. |
| `IHostingFiles` and `IZpamDownloadSource` | Publish each server's private home; stream bounded, verified downloads and expose byte progress. Bind status and admin to the owned local endpoint. Preserve unrelated server files and use atomic creation for exports. Share the install interlock with game launch and stop. |
| `IMediaFiles` and `ICod2xFiles` | Resolve regular files under the verified game home. Compare or publish a replay alias in one operation. Refuse device paths, reparse points and alternate streams. Keep licensed base data read-only. Require the expected content identity before deleting or replacing a staged asset. |
| `ICrashReportService` and `ICrashReportLease` | Read a verified, bounded log and sidecar. Serialize the ledger and transfer to the configured DSN with the supplied deadline and with redirects, cookie storage and ambient credentials disabled. Supply fixed release/edition and validated OS version and PC model fields. Route process output through `GameSessionLog` before writing it. |
| `ILauncherReleaseSource` and `IWindowsLauncherUpdate` | Select the installer asset from the signed Windows release inventory. Check the hash and publisher during staging, then take the shared launch/install lease and recheck both game and server ownership. Handoff to the native installer and preserve user data. |


The Windows build passes raw command text to `WinMain`. The planner quotes
the executable and preserves the engine's own double quotes in the tail.
It normalizes Windows path separators in engine values so a trailing backslash
cannot escape the closing engine quote. The process adapter publishes the
generated join config with a user-private ACL before spawning. It removes the
file on a failed start and after use, with recovery for interrupted launcher
sessions. Join config contents may include a password. Engine output and crash
capture need redaction. [CreateProcessW](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)

The catalogue verifies the original index bytes with its configured public key.
A network outage can use a reverified cache. A valid response may repeat a
revision only if its index bytes match. The selected profile becomes an import
bundle with signed bytes and provenance. Its script dependencies remain unresolved.
[Reparse points](https://learn.microsoft.com/windows/win32/fileio/reparse-points-and-file-operations)
[LockFileEx](https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-lockfileex)
[ReplaceFileW](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew)

Construct a `ConfigImportSession` with the setup's source platform and the
launcher's destination platform. Windows uses `ConfigImportPlatform.Windows` as
its destination. The session re-reviews the composed config and hands one
compare-and-commit request to `ConfigProfileStore`. Coordinate engine ownership
with the store's lock during replay and writes. Run blocking profile operations
off the WinUI thread. A retry after a recovered import returns the existing
receipt from history.
[MoveFileExW](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-movefileexw)
[PostDeleteItem](https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-postdeleteitem)

`ConfigShareService` sends only prepared, validated setup text and records the
withdrawal token from a successful reply. If saving fails, it returns the
receipt so the caller can retry the private write. A confirmed withdrawal clears
the matching receipt. The portable HTTP adapter uses a whole-request deadline
and refuses redirects.
[HttpClient completion and deadlines](https://learn.microsoft.com/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)
[Redirect behavior](https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.allowautoredirect?view=net-10.0)

Hosting uses `HostedServerJson` to import the existing settings list. The server
process adapter uses `HostingConfig.Render` and the existing dedicated launch
plan. Supply the owned-process check to `HostedAdminQueue` before dispatching
commands over loopback. `ZpamPackage` checks archive size and SHA-256, selects
bounded regular files with `ZipArchive`, and publishes them through private staging.
[ZIP entry metadata](https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchiveentry?view=net-10.0)

The release offer takes an expected archive name from its caller. Windows
packaging defines that name and verifies the installer publisher or server ZIP
signature before deployment. The core checks release metadata and SHA-256.


## First WinUI unit

Keep `CoD2Unbound.sln` portable. Add the approved shell under
`dotnet/CoD2Unbound.WinUI/`, put process, filesystem, credentials, activation,
installer and network adapters in `dotnet/CoD2Unbound.Windows/`, and collect
Windows-only tests in `dotnet/CoD2Unbound.Windows.Tests/`. A separate
`dotnet/CoD2Unbound.Windows.sln` can include those projects and the shared
core. Start with Setup and Game Data, then Home and Play including both
editions and the already-running path. Follow with Servers and Hosting.
Bind Config Studio, Library and Settings as their canvas flows are approved.
Build against the [view model contracts](PORT-MAP.md#view-model-contracts).

Use Windows 11 x64, the .NET 10 SDK, Windows SDK 10.0.26100 or a newer SDK
supported by the pinned framework, and Windows App SDK 2.7.0. Set up Visual
Studio 2026 with WinUI application development or Windows App Development CLI
0.6+ with the C# WinUI template. Use a standard-user account plus explicitly
licensed Steam and retail installations. Bring both editions' client and
dedicated executables, the shared launch/install identity, and test package
signing credentials for activation and update checks. Developer Mode applies
to the local setup described in the quickstart. [Microsoft WinUI
quickstart](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/get-started-winui3-for-desktop)
[Windows App SDK stable channel](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
[Windows target framework example](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here?pivots=winui3-packaged-csharp)

The portable core, headless fixtures and portable view models compile and run
under the pinned Linux SDK container. macOS can regenerate the Swift fixtures
headlessly. WinUI XAML compilation, package identity and activation, native
adapter behavior and UI Automation require Windows. A physical Windows PC is
needed to accept the GPU, focus, keyboard, audio and display behavior of the
running game and launcher.

### Windows launcher updates

Set `UpdateBuildProfile=Public`, `EnableLocalUpdateFeed=false`, and
`UpdatePublisherRules=<ROOT_SHA256>=<PUBLISHER_PROFILE_EKU_OID>` when publishing.
Use the 64 hex digits of the SHA-256 fingerprint of the root certificate
Windows accepts for the Setup signer. Use the certificate profile's unique
extended key usage OID in the private enterprise or UUID namespace. The leaf
must explicitly carry both this usage and the ordinary Code Signing usage.
Windows validates the chain, including the intermediate and timestamp. Public
updates require an available revocation result. Until a publisher is
configured, the launcher can check for releases and open the download page.

Publish the launcher and Setup with the same three-field version. Routine leaf
or intermediate rotation under the same root and profile needs no rule change.
For a root or profile change, first release a transition launcher with
`UpdatePublisherRules=<OLD_ROOT_SHA256>=<OLD_PROFILE_EKU_OID>|<NEW_ROOT_SHA256>=<NEW_PROFILE_EKU_OID>`.
Each rule matches a root and profile together. Publish only public releases with
this signing identity. Sign fixtures and prereleases with a separate identity.

For an offline test build, set `UpdateBuildProfile=Fixture` and
`EnableLocalUpdateFeed=true` when publishing and
set `COD2_UNBOUND_TEST_UPDATE_ORIGIN` in the launcher process environment to a
`127.0.0.1` origin. Serve the feed at `/releases` and the exact Setup filename
and `SHA256SUMS` at `/releases/download/v<version>/`. Use a test signer trusted
by the test machine and configure its root and test profile OID in the test
launcher. The loopback feed uses cache-only revocation retrieval.

### Windows reporting configuration

Pass the two optional MSBuild properties alongside `UpdatePublisherRules` when
publishing the launcher.

| Property | Release value |
| --- | --- |
| `UsagePostHogKey` | `phc_<EU_INGESTION_KEY>` |
| `CrashReportDsn` | `https://<PUBLIC_KEY>@<SENTRY_INGEST_HOST>/<PROJECT_NUMBER>` |

Omit a property to omit that consent step and Privacy switch. Only an explicit
yes allows the relevant reports to leave this PC. Usage requests use the EU
ingestion endpoint without redirects, cookies, or ambient credentials. The
service configuration retains country only and disables person profiles.
Crash reports use a separate Windows text attachment containing exception
details, module offsets, and module versions. Local game logs and dumps stay
on this PC.

Keep `UpdateBuildProfile=Public` and `EnableLocalReportReceivers=false` for
releases. Test builds additionally require `UpdateBuildProfile=Fixture` and
`EnableLocalReportReceivers=true`. Supply dummy property values and point
`COD2_USAGE_URL` and `COD2_CRASH_URL` at individual receivers on
`127.0.0.1`. A test build with missing or invalid receiver settings skips the
affected service. Public compilation and packaging reject fixture support.
