# Windows release and updates

## Build and sign

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

```sh
test "$(docker image inspect cod2-build-win --format '{{.Id}}')" = \
  sha256:eb9d795192ea5bf14d58d939dd76b2c3e4fe344f2a91b713093ba5a4d728b7e6
mkdir -p output/r05/package
DOCKER_BUILDKIT=0 docker build --pull=false --platform linux/amd64 \
  --iidfile output/r05/package/image.id tools/windows/package
export COD2_WINDOWS_PACKAGE_IMAGE="$(cat output/r05/package/image.id)"
tools/windows/release.sh test
```

The test mode creates disposable self-signed code-signing and local RFC 3161
timestamping certificates, then removes the private keys. The public
certificates and verification output are under `work/`. The package includes
an Authenticode-signed installer and embedded uninstaller, signed engine PEs
and a dedicated-server ZIP with an adjacent signed catalog. The catalog
contains the SHA-256 of the exact ZIP bytes. To verify a download on Windows,
verify the catalog's Authenticode signature with `signtool verify /pa /v archive.cat`
(or the Digital Signatures tab), then match the member's SHA-256 shown by
`certutil -dump archive.cat` to `Get-FileHash archive.zip -Algorithm SHA256`.
The ZIP also contains per-file checksums for use after extraction.

For production, use `tools/windows/release.sh azure` with these environment variables.

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
release run so it remains valid through signing. Publish the files in
`artifacts/` only after the command and the Windows verification pass.
[Artifact Signing integrations](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations) · [Jsign Artifact Signing](https://ebourg.github.io/jsign/#trusted-signing) · [Microsoft Public Trust model](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-trust-models)

The package pins Inno Setup 7.1.1 x64 and Jsign 7.6 by SHA-256. ISCC runs under
Wine with Xvfb. Inno first emits an uninstaller for signing and then embeds
the signed image in Setup. The uninstaller carries the release ProductVersion
and retains Inno's internal FileVersion for its replacement logic.
Authenticode timestamps apply to signed releases,
so use the unsigned `layout/` when comparing reproducibility.
[Inno signed uninstaller workflow](https://jrsoftware.org/inno-setup/documentation/topic_setup_signeduninstaller.htm) · [Jsign 7.6](https://github.com/ebourg/jsign/releases/tag/7.6)

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
a second process that locks a managed engine image. A user can always
resume a hosted server after the operation using its original writable home.

The launcher integration point is `layout/launcher/CoD2Unbound.exe` and
its publish output. When the launcher lands, add its publish directory to the
release staging step and validate its PE imports and version resources there.
Inno uses the launcher as the Start menu entry only when the executable exists.
The same condition adds `.dm_1` and `.cfg` Open With entries and an opt-in
for the `cod2x:` and `cod2:` handlers where available. Existing defaults
and third-party URL handlers are preserved. The names match the Mac bundle.
The launcher must validate incoming links and route configs to import review.

WinUI 3 supports an unpackaged self-contained deployment from a custom
installer. MSIX also supports App Installer updates and manifest associations.
Its package identity and default registry/file virtualization would need
additional cross-process validation for the launcher and its separate engines.
The conventional installer supplies one replaceable payload with direct Win32
registration, and the server ZIP is independently deployable.
[Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps) · [MSIX App Installer](https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-file-overview) · [MSIX virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)

## Launcher updater contract

The Mac launcher checks GitHub Releases on demand and at most daily when
automatic checks are on. It matches the Mac asset with its SHA-256, downloads on
request, checks the signature, swaps the bundle and restarts. It defers while
a game runs and sends the player to the release page when the install cannot
be replaced.

- Use the same check cadence and consent on Windows. Select the installer for
  Windows 11 x64 and compare its numeric version with the installed
  `RELEASE_VERSION`. The Add/Remove Programs entry exposes the same version.
  Check the release asset's SHA-256 and Authenticode trust, timestamp, and
  expected publisher before running it. Keep downloads in private staging.
- Defer while the launcher owns a running client or hosted server, and combine
  that state with a check for separately started processes using the install.
  Let players stop sessions in the normal way. Disable starting new sessions
  once an update has been handed off. For a machine-wide install, hand off to
  Setup with the existing scope and let Windows request elevation.
- Have a helper outside the install directory wait for the launcher to exit,
  run the staged installer with the silent options above and wait for it to
  finish. Re-read the installation version and start the launcher only on
  success. On failure show the Setup log and offer a fresh run of the same
  signed installer as repair. An explicit downgrade, if offered later, must
  use a previously verified artifact and require the player's choice.
- Update a portable server independently by verifying and extracting the new
  ZIP beside the old binaries, stopping the server, and starting the updated
  edition with the same game-data and instance paths. Retain the prior
  binaries until the new server runs.

Website release data needs platform `windows`, architecture `x64`, minimum
OS Windows 11, version and download URLs, SHA-256 and byte sizes for the
installer, server ZIP and catalog, plus the publisher identity and licensed
game data prerequisite. Expose the ZIP and catalog together and label the
server download separately from the installer. Keep macOS assets in their
current release slots.

## Offline proof

Pass a fresh directory produced by `release.sh test`, a lower-version
test-signed installer, and optionally the licensed data directory to
`tools/windows/check-package.sh`. Keep the resulting logs with the release.
Wine exercises installer behavior, loopback server queries and file
preservation. Windows 11 still needs testing for actual elevation, default
handlers, publisher trust, SmartScreen, Smart App Control and Defender.
