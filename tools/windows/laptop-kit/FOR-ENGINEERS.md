# Windows laptop evidence for engineers

Each run writes a dated directory under results. The text checklist records what the tester saw and any skipped or blocked cases. The temporary session directory is removed after the evidence and before/after comparisons have been written. Copy any needed local demo out of that directory before ending the run and review it separately.

## Earlier Windows VM coverage

The guest ran Windows 11 ARM64 build 26300.9457 with Windows PowerShell 5.1.26100.9457, emulated x64 engine binaries and a 640 × 480 Microsoft Basic Display Driver. The lab wrapper supplied the x64 guard value in the script process and used a restricted token for the signed-in account. Native script checks covered manual folder selection, missing archives, install junction resolution, profile junction rejection, Credential Manager, both extracted ZIP servers, the two standing Toujane replays and all 22 four-player replay runs. Original 1.3 drew menu and Toujane captures in Direct3D 9 and DirectX 7 mode. CoD2x exited before graphics initialization with WMI error 0x80004005 on `Win32_BaseBoard.Manufacturer`. The guest exposed zero baseboard rows even to SYSTEM. The laptop must establish CoD2x client startup and hardware presentation.

Both kit installers use the regular Inno Setup 7.1.1 script with `x64compatible`. The previous Setup carries the 0.3.9 launcher and both engines for the upgrade check. On Arm, confirm that Setup offers Continue and Cancel before installing. On x64, Setup goes straight to its usual pages. The kit stops its test server with an authenticated loopback command after the busy check. In an earlier VM run, installed Original loaded Toujane and switched DX9, DX7 and DX9 after disconnecting to the menu. Installed CoD2x hit the same WMI error as the portable binary.

Native Authenticode reported an untrusted certificate root, Defender and real-time protection were on, and Smart App Control was in Evaluation. The UTM file transfer provided no Internet zone metadata. Check SmartScreen and the shipped installers on the x64 laptop.

## Essential evidence

| Check | Where it came from | Files and what to look for |
| --- | --- | --- |
| Kit copy, machine and package signature | Windows build and server verification, packaging reports and the first client kit | kit-checksums.txt confirms the outer manifest. computer/windows-and-graphics.json records the OS build, GPU, driver and PowerShell version. computer/dxdiag.txt is the filtered DirectX report. computer/windows-security.json and signatures.json record Windows Security and native Authenticode status. A test signer may be untrusted. server-catalogue.txt checks for the ZIP digest in the catalogue dump. It does not prove public publisher trust. |
| Discovery and storage | Paths laptop checklist and Windows game-data contract | discovery-automatic records the selected source or the no-data diagnostic. discovery-chosen-folder, discovery-missing-archives, discovery-junction and discovery-unsafe-profile cover precedence, archive validation, resolved install links and refusal to write through a profile junction. retail-paths.json lists InstallPath values in both registry views. game-files-unchanged.txt compares the selected game tree and VirtualStore, plus the retail tree when selected. Before/after file lists stay out of the results. Review paths that remain in diagnostics. |
| Credential Manager | Paths laptop checklist | key-seed, key-check and key-clear hold exit.json and filtered output from test-key.exe. A zero exit for check exercises valid and corrupt explicit credentials and restores the valid sample. The corrupt credential must suppress legacy fallback. Script access is serialised for the Windows user. The runner checks for the conventional credential name before writing and only clears the sample via the helper. Owned-key authentication and real key precedence require a separate controlled account. |
| Server ZIP and replay | Build and rounding reports, both replay laptop lists, packaging reports | server-original and server-cod2x contain the UDP replies, exit and 200-tick trace after running the ZIP payload with its sample config. getinfo is 118 for both. getstatus is 118 for Original and 120 for CoD2x. replay-*-standing contains the 600-tick trace, player readings and entity dump. The automatic comparison checks the state/hash and player readings against the corresponding bundled reference. |
| Four portable clients | First client kit and Windows input checklist | client-original-dx9, client-original-dx7, client-cod2x-dx9 and client-cod2x-dx7 contain kit-menu.jpg, kit-toujane.jpg, exit.json and console output. Read the Direct3D code-path message to confirm the selected graphics path. Briefing orientation, recovery, controls and sound depend on the tester's observation. |
| Install lifecycle | First and follow-up packaging reports | setup-previous, setup-busy-upgrade, setup-upgrade, setup-repair and uninstall-current-user contain Setup logs. setup-busy-server includes replies and the rejected upgrade exit code. The old server must remain running with the previous registered version and binary digest. The installed signature receipts cover the uninstaller, launcher and payload catalogue. setup-summary.txt records removal and preservation of the test config, screenshot, demo and credential and the old disc location. |

automatic-checks.txt records the script's stage verdicts and failure detail. A successful stage reports its own extent. A run may finish after a failed or skipped check. For a game process, exit.json contains the child exit code and timeout flag. Filtered stdout.txt, stderr.txt and console logs locate the last action before a failure. Inspect the manual notes as well as the automatic line before evaluating acceptance.

## Extras evidence and expected comparisons

| Group | Origin and evidence |
| --- | --- |
| More checks on this PC, another keyboard layout and a second screen | The input laptop checklist and report cover detailed mouse, layout, focus, windows, gamma, process CPU and audio endpoint checks. controls-* contains each run's console log and any kit screenshots. Reusing a menu choice reuses its private profile during that run. Record the actual rendering mode, display/scaling, key layout, pointer configuration and endpoint. The optional 11 scenarios per edition use replay-*-case folders and record state, players and entities. They check completion and player count. Retail parity on every scenario still needs a paired capture. |
| A second PC on the same network | The build and input laptop lists cover LAN browser and direct play. lan-*-host has replies, gameplay log and exit. lan-*-browser, lan-*-join and lan-*-host-player have client logs. Compare browser status, occupancy, version, PunkBuster and the effect of cl_hideServerIp. Record which PC was host and test the paths with no default gateway. |
| A second player | Both replay laptop lists and the input report cover movement, weapon behaviour, live HUD, objectives and voice. Compare paired sessions through the normal opening and grace period. Brecourt planting/defusing should take roughly five/ten seconds. CoD2x's competitive preset sets g_competitive 1 and sv_fps 40. Keep the first normal post-join update and packet/snapshot receipts from cl_shownet 2. If an independent decoder is available, correlate server time, sequence, first decoded delta, base snapshot and commandTime with the first visible correction. The kit does not stall a peer or decode snapshots. A reviewed local demo can supplement those receipts. |
| A retail disc install | The rounding and input reports and both replay laptop lists supply the anchor. retail-mouse/retail-files.json records the retail executable, graphics and Miles digests. retail-mouse/comparison.txt reports exact equality of the 4,096 rows. retail-replay/capture.bin and recorder.log contain the exact retail 1.3 600-tick capture for the engineering comparator. reference-dedicated records startup, dvarlist, queries and shutdown using the source-tag CoD2x 1.4.6.8 DLL with unchanged 512 MiB hunk. reference-server-join is optional. Full reference runs skip when a legacy key exists. The probes copy only licensed inputs into the test directory. |
| A spare Windows account or a lab PC | The paths and packaging reports supply these cases. Record secondary-only Steam, both HKLM registry views (32-bit wins), no-data diagnostics and expected rejection paths. Record protected install and VirtualStore canaries, Unicode and extended-length paths, redirected LocalAppData and OneDrive Documents/Desktop. Check the real config, screenshot and demo in the temporary profile during the run. All-users Setup writes setup-all-users and uninstall-all-users evidence. Record UAC, machine-wide Start entries, Program Files and their removal. Recheck real publisher trust and reputation on the eventual public download. |

## Launcher evidence

Part A checks the installed Start shortcut and opens its target with a disposable
launcher profile, loopback discovery, an empty local release list and the signed
catalogue fixture. It checks that first-run Setup records the chosen game folder.
The `launcher` results include PNGs, display sizes and theme settings, filtered
engine logs and what the tester saw when launching both editions. The script
checks that uninstall preserves the test settings before removing the scratch.
Part B covers layouts at larger scaling, snapping and keyboard and Narrator use.

## Resumable laptop runner

`Run-All.ps1` starts the updated essential run. Each directory under
`results/<run>` has an atomic `result.json` with the label, disposition,
elapsed seconds and explanation. A transport process can disconnect without
ending the scheduled Windows worker. Changes require the exact response
`YES`. An interrupted ordinary probe can be retried. An interrupted installer
step retains evidence for cleanup before a new run. `-Extras` and `-LanOnly`
use the existing independent controls.

`drive.sh` uses OpenSSH public-key authentication with a pinned host key and
bounded commands and transfers. Set `COD2_KIT_SSH_IDENTITY`,
`COD2_KIT_SSH_PORT` or `COD2_KIT_SSH_KNOWN_HOSTS` to supply alternate values.
The UTM backend is scoped to the existing scratch UUID and takes the SID of
its already signed-in standard account through `--utm-sid`. Both run the same
`Remote-Kit.ps1` scheduled task under an interactive Windows token. The Mac
pulls a checksum-checked ZIP whose embedded receipt matches each finished
step. Keep the same Mac output path to recover a pending download. Task
registration is temporary. Windows security dialogs require the person in
front of the laptop.

The frame comparisons use the saved Toujane and Harbor demos from the
earlier test runs. Metrics are RGB deltas at the sampled view, with the
current image on the left and the named baseline on the right. Matching
camera positions matter more than exact color. Wine's Harbor skies, distant
water and fog already differed from the Windows software adapter. New driver
differences can be legitimate. Review the paired PNG and console before
calling a graphics fault.
