# Windows release and updates

## Prepare the release commit

The Windows release targets x64 PCs running Windows 10 (22H2) or Windows 11.
Setup enforces build 19045. Check that `RELEASE_VERSION` and the Windows
launcher's project `Version` match before publishing. The updater uses the
launcher assembly's version and Setup uses `RELEASE_VERSION`.

Run the commands from the Windows release checkout. The project uses its
local x86-64 MinGW and Wine image at
`sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6`.
Set `COD2_WINDOWS_COMPANY` to the intended Windows publisher display name.
The product is CoD2 Unbound and the version comes from `RELEASE_VERSION`.

```sh
export COD2_WINDOWS_COMPANY='Publisher name'
tools/windows/release.sh layout
```

The command prints a fresh directory under `output/r05/package/`. Its
`layout/` contains the unsigned, reproducible payload. The same source
revision, company, and build image produce the same file bytes. The build
normalizes file timestamps and fixes the engine's build date to the source
revision. It checks x64 PE imports using the Windows matrix allow-lists.
SDL2 and curl are statically linked. There are no app-local DLLs in the engine
payload. Windows provides the Direct3D 9 device and compiler, which compiles
shaders from the player's licensed game archives.

Use the following local package image for the compiler, signer and verifier.
Check the base image ID before building and retain the resulting image ID for
subsequent builds and tests.

From the clean release commit, run `sh tools/windows/vm-build.sh --export-x64`
with the Windows build VM already running. Pass
`--publisher-rules "$(cat /path/to/publisher-rules.txt)"` when publishing the
Public launcher with self-update enabled. The rules pin the accepted trust
root and the publisher's certificate-profile EKU. Use the same rules in the
publisher verification step below. The wrapper runs the portable core tests,
publishes x64 and ARM64, and prints the path of `launcher-win-x64.zip` on the
Mac. Give that ZIP directly to the Docker release command. The wrapper
compares its SHA-256 before and after transfer. Docker verifies every file
listed in the ZIP's `SHA256SUMS`, checks the x64 app host, and records the
Windows publish's Git revision and input hashes under `work/`. Use a publish
from the same release checkout.

```sh
test "$(docker image inspect cod2-build-win --format '{{.Id}}')" = \
  sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6
mkdir -p output/r05/package
DOCKER_BUILDKIT=0 docker build --pull=false --platform linux/amd64 \
  --iidfile output/r05/package/image.id tools/windows/package
export COD2_WINDOWS_PACKAGE_IMAGE="$(cat output/r05/package/image.id)"
pack_mode=test
launcher=/path/to/launcher-win-x64.zip
tools/windows/release.sh "$pack_mode" "$launcher"
```

The test mode creates disposable self-signed code-signing and local RFC 3161
timestamping certificates, then removes the private keys. The public
certificates and verification output are under `work/`. The package includes
an Authenticode-signed installer and embedded uninstaller, plus signed engine
and launcher EXEs and DLLs. Setup contains a signed `payload.cat` that lists
their paths and raw hashes after signing. The dedicated-server ZIP has its
own signed catalog that binds the exact ZIP bytes. To verify that download on Windows,
verify the catalog's Authenticode signature with `signtool verify /pa /v archive.cat`
(or the Digital Signatures tab), then match the member's SHA-256 shown by
`certutil -dump archive.cat` to `Get-FileHash archive.zip -Algorithm SHA256`.
The ZIP also contains per-file checksums for use after extraction.

## Switch to the production signer

In the authorized release environment, set `pack_mode=azure` for the same
release command and supply the Artifact Signing configuration below. Keep the
access token out of the launcher, the ZIP and release logs.

| Variable | Required value |
| --- | --- |
| `COD2_SIGN_ENDPOINT` | Regional HTTPS Artifact Signing service origin |
| `COD2_SIGN_ACCOUNT` | Artifact Signing account name |
| `COD2_SIGN_PROFILE` | Public Trust certificate profile name |
| `COD2_SIGN_TOKEN` | Access token issued for the Artifact Signing resource with the certificate-profile signing role |

Obtain the token in the authorized release environment, using the exact
Artifact Signing token audience documented by Jsign. Jsign reads it directly
from the environment and performs RFC 3161 timestamping. The verification
step uses the pinned Microsoft Public Trust root and checks both certificate
chains, the timestamp and the payload digests. Refresh the token before a
release run so it remains valid through signing. The `azure` mode requires
a Public launcher with publisher rules and refuses fixture launchers.
[Artifact Signing integrations](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations) · [Jsign Artifact Signing](https://ebourg.github.io/jsign/#trusted-signing) · [Microsoft Public Trust model](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-trust-models)

The package pins Inno Setup 7.1.1 x64 and Jsign 7.6 by SHA-256. ISCC runs under
Wine with Xvfb. Inno first emits an uninstaller for signing and then embeds
the signed image in Setup. The uninstaller carries the release ProductVersion
and retains Inno's internal FileVersion for its replacement logic.
Authenticode timestamps apply to signed releases,
so use the unsigned `layout/` when comparing reproducibility.
[Inno signed uninstaller workflow](https://jrsoftware.org/inno-setup/documentation/topic_setup_signeduninstaller.htm) · [Jsign 7.6](https://github.com/ebourg/jsign/releases/tag/7.6)

## Verify and stage the downloads

1. Take the four files from the run's `artifacts/` folder. Record each
   filename, byte count and SHA-256. Run `shasum -a 256 -c SHA256SUMS` from
   that folder to verify Setup, the server ZIP and its catalogue.
2. On Windows with default protection, use
   `signtool verify /pa /v /tw <file>` on Setup and the server catalogue.
   Verify the publisher rule using the Windows .NET 10 SDK and
   `tools/windows/package/verify-publishers.ps1 --setup <Setup.exe> --rules-file <rules.txt> --other-publisher <reference.cer>`.
   Include each retained Setup needed to exercise certificate renewal and a
   certificate from an independent publisher on the same service. Compare
   the catalogue's SHA-256 member with the exact ZIP bytes.
3. On Windows 10 (22H2) and Windows 11 x64, use a standard account to
   install from the previous public release, update from the launcher,
   repeat Setup to repair, and uninstall. Use the Start shortcut and
   Setup's final Launch option. Check Settings, loadouts, screenshots,
   demos, the CD key and licensed game files after uninstall. Check the
   default-app choices and launches for both editions. Record the publisher
   and any Windows security prompts.
4. After sign-off, stage the exact same four files on both release
   origins. Put the website copy under `/releases/v<version>/` and update
   `/update/releases.json` after the files are in place. Verify the listed
   byte sizes and the download hashes against the staged files on each
   origin before announcing the release.

## Installation and launcher integration

Default installation is per user in `%LocalAppData%\Programs\CoD2 Unbound`.
All-users installation uses `%ProgramFiles%\CoD2 Unbound`. Repair by running
the same installer in the existing scope. Upgrades keep the same application
ID and location. The installer leaves settings and game files in
`%LocalAppData%\CoD2 Unbound` and leaves the CD key in Credential Manager.
See [Windows game data and user files](windows-game-data.md) for discovery.

Use this installer command for the current-user silent path. Substitute
`/ALLUSERS` only for a machine-wide install. Keep the log with an error report.

```text
CoD2-Unbound-<version>-windows-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /LOG="C:\path\setup.log"
```

Stop the launcher, all clients and all installed dedicated servers before
repair, update or uninstall. Setup checks the installed process images for
exclusive write access before it starts replacing files. This also catches
a second process that locks a managed engine image. If Setup cannot move
the previous installation after retrying, close programs using its
installation folder and try again. The previous installation stays in place
when that first move fails. A user can always
resume a hosted server after the operation using its original writable home.

Setup places the x64 publish under `layout/launcher` during packaging and
installs it beside the `original` and `cod2x` engine folders. The **CoD2 Unbound**
Start shortcut opens `launcher\CoD2Unbound.Windows.exe`. Setup adds
`.dm_1` and `.cfg` Open With entries and an opt-in for available `cod2x:`
and `cod2:` handlers. Existing defaults and third-party URL handlers are
preserved. An activated file or link opens in the existing launcher window
when one is running. Configs open the import review. Demos and server links
show the requested game edition and wait for Play or Connect. Requests that
arrive during Setup or a dialog wait for it to finish.

The WinUI 3 launcher uses unpackaged, app-local Windows App SDK 2.7.0 and
self-contained .NET 10. Keep all the published libraries and resources next
to the launcher. Rebuild Setup to deliver runtime security updates. MSIX
also supports App Installer updates and manifest associations.
Its package identity and default registry/file virtualization would need
additional cross-process validation for the launcher and its separate engines.
The conventional installer supplies one replaceable payload with direct Win32
registration, and the server ZIP is independently deployable.
[Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) · [MSIX App Installer](https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-file-overview) · [MSIX virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)

## Launcher updater contract

The launcher checks on demand and at most daily when automatic checks are
on. It checks GitHub first and uses cod2unbound.com for release lists, Setup
and SHA256SUMS when the primary transfer fails. Home's news also uses the
website on a transport failure. A GitHub news 404 clears news. The launcher
checks the release size, SHA-256, Authenticode trust, timestamp, publisher
and Setup version from either source before launching the staged copy.

- Stop any running game or hosted server before installing an update. The
  launcher also checks separately started processes from the installation.
  In-app installation supports the default per-user location. For a
  machine-wide installation, download Setup and run it in the existing
  all-users scope.
- Verified Setup starts from the private staging folder, waits for the
  launcher process to exit, and takes a copy of the old installation before
  replacing files. Setup reopens the launcher after a successful upgrade or
  a handled failure. If Setup is interrupted, the next launcher startup can
  use the verified installer to restore the old version. If the launcher
  cannot start or verified Setup is gone, download and run Setup again in
  the existing scope. Keep the Setup log for troubleshooting.
- Update a portable server independently by verifying and extracting the new
  ZIP beside the old binaries, stopping the server, and starting the updated
  edition with the same game-data and instance paths. Retain the prior
  binaries until the new server runs.

Website release data needs platform `windows`, architecture `x64`, minimum
OS Windows 10 (22H2) or Windows 11, version and download URLs, SHA-256 and byte sizes for the
installer, server ZIP and catalog, plus the publisher identity and licensed
game data prerequisite. Expose the ZIP and catalog together and label the
server download separately from the installer. Keep macOS assets in their
current release slots.

## Offline proof

Pass a fresh directory produced by `release.sh test`, a lower-version
test-signed installer, and optionally the licensed data directory to
`tools/windows/check-package.sh`. Keep the resulting logs with the release.
Wine exercises installer behavior, loopback server queries and file
preservation. Wine 9's Windows 10 preset reports build 19043, below the
installer floor. Run package maintenance in Wine with its Windows 11
preset. Windows 10 (22H2) and Windows 11 still need native x64
checks for actual elevation, default handlers, publisher trust, SmartScreen and
Defender. Check Smart App Control on Windows 11 where available.
