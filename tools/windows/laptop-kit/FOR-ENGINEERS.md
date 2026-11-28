# Windows laptop evidence for engineers

Each run writes a dated directory under results. The first line of automatic-checks.txt records the Windows release, edition and build. The text checklist records what the tester saw and any skipped or blocked cases. The temporary session directory is removed after the evidence and before/after comparisons have been written. Copy any needed local demo out of that directory before ending the run and review it separately.

## Earlier Windows VM coverage

The guest ran Windows 11 ARM64 build 26300.9457 with Windows PowerShell 5.1.26100.9457, emulated x64 engine binaries and a 640 × 480 Microsoft Basic Display Driver. The lab wrapper supplied the x64 guard value in the script process and used a restricted token for the signed-in account. Native script checks covered manual folder selection, missing archives, install junction resolution, profile junction rejection, Credential Manager, both extracted ZIP servers, the two standing Toujane replays and all 22 four-player replay runs. Original 1.3 drew menu and Toujane captures in Direct3D 9 and DirectX 7 mode. CoD2x exited before graphics initialization with WMI error 0x80004005 on `Win32_BaseBoard.Manufacturer`. The guest exposed zero baseboard rows even to SYSTEM. The laptop must establish CoD2x client startup and hardware presentation.

Both kit installers use the regular Inno Setup 7.1.1 script with `x64compatible`. The previous Setup carries the 0.3.9 launcher and both engines for the upgrade check. On Arm, confirm that Setup offers Continue and Cancel before installing. On x64, Setup goes straight to its usual pages. The kit stops its test server with an authenticated loopback command after the busy check. In an earlier VM run, installed Original loaded Toujane and switched DX9, DX7 and DX9 after disconnecting to the menu. Installed CoD2x hit the same WMI error as the portable binary.

Native Authenticode reported an untrusted certificate root, Defender and real-time protection were on, and Smart App Control was in Evaluation. The UTM file transfer provided no Internet zone metadata. Check SmartScreen and the shipped installers on the x64 laptop.

## Essential evidence

| Check | Where it came from | Files and what to look for |
| --- | --- | --- |
| Kit copy, machine and package signature | Windows build and server verification, packaging reports and the first client kit | kit-checksums.txt confirms the outer manifest. computer/windows-and-graphics.json records the OS release, edition, build, GPU, driver and PowerShell version. Smart App Control status is available on supported Windows 11 PCs. computer/dxdiag.txt is the filtered DirectX report. computer/windows-security.json and signatures.json record Windows Security and native Authenticode status. A test signer may be untrusted. server-catalogue.txt checks for the ZIP digest in the catalogue dump. It does not prove public publisher trust. |
| Discovery and storage | Paths laptop checklist and Windows game-data contract | discovery-automatic records the selected source or the no-data diagnostic. discovery-chosen-folder, discovery-missing-archives, discovery-junction and discovery-unsafe-profile cover precedence, archive validation, resolved install links and refusal to write through a profile junction. retail-paths.json lists InstallPath values in both registry views. game-files-unchanged.txt compares the selected game tree and VirtualStore, plus the retail tree when selected. Before/after file lists stay out of the results. Review paths that remain in diagnostics. |
| Credential Manager | Paths laptop checklist | The runner assigns a private slot per run. The key receipt contains both empty-slot client starts and the seed and verify exits from test-key.exe. Cleanup verifies the slot is gone. Preflight and cleanup record only presence and last-write time for the regular key. The offline Wine path check also exercises invalid explicit credentials in a test slot. |
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

The Servers navigation receipt records the selected row's UI Automation runtime ID before, during and after a refresh against 252 loopback addresses. Hosting records the launcher sheet and Windows folder picker separately, with a canceled row export and a completed page export in each appearance. Cleanup records the demo Open With key across a plain uninstall and a second uninstall with a test entry for another app.

The exclusive checks stop if another program's dialog has the foreground. The receipt names the program. A map-load timeout can save a scrubbed console tail and the kit game's x64 stack traces under `private-map-stall-*`. Keep these in the private per-step receipts. The two results packers omit them from combined archives.

Build `StackProbe.c` in the kit's x64 MinGW toolchain with `-municode -ldbghelp` and pass its executable to `assemble.py --stack-probe`.

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


## Part B — Extras, when you have them

Each group can be done another day. Record the screen resolution and scaling, keyboard layout and sound devices you use. Run the same command with `-Extras` at the end and choose a group by number. Take notes as you go, restore any Windows settings you change, and pack the extra results the same way.

### 1. More checks on this PC · 35–55 minutes

- **DirectX 7 mode.** Open each edition in DirectX 7 mode from the game menu. Play Toujane and take both pictures using the laptop guide. Check that the picture returns after switching from Direct3D 9 to DirectX 7 mode and back.
- **Mouse.** Try Original 1.3 with both mouse options on the script menu and CoD2x with `m_rinput` set to 0, 1 and 2. Move between two marks 10 cm apart slowly and quickly. Try changes to Windows pointer speed and **Enhance pointer precision**. Original’s regular mouse should follow Windows settings, and the direct mouse modes should stay consistent. For CoD2x modes 1 and 2, note `m_rinput_hz` and `m_rinput_hz_max` while moving and again while stopped. Try menus in windowed and fullscreen play, reopen a menu to check pointer position, and try both side buttons and slow and fast wheel turns in both directions if your mouse has them. Note missed releases, extra scroll steps, jumps or drift.
- **Keys and focus.** Try the console key, arrows, **Num Lock**, keypad Enter, left and right modifiers, Backspace held down, and typed and pasted chat. Check saved bindings with `/bindlist`. Switch away while holding a movement key and release it on the desktop. Try the Windows key and Alt+Enter with `developer` set to 0 and 1. Movement should stop when released and saved keys should work after you return.
- **Display recovery.** Change anti-aliasing to an available value, switch between windowed and fullscreen play, Alt+Tab twice and minimise and restore. Use `/disconnect` to return to the menu before applying graphics changes or running `/vid_restart`. Switch graphics modes with `/set r_rendererPreference dx7` and `/vid_restart`, then reopen the map and repeat with `dx9`. **F10** saves an extra picture. Move the window and restart using the same game menu choice to check its restored position. Note `vid_xpos` and `vid_ypos`. Try resizing CoD2x and running in a window the size of the desktop. Watch for misplaced clicks, black screens and a jump on the first mouse movement after returning. For the Task Manager comparison, note CPU usage over ten seconds active and minimised with the same `r_swapInterval` in both games. Repeat with `com_maxfps 85` and `com_maxfps 125`, then restore both settings.
- **Sound devices.** Listen to menu music, footsteps and gunfire from the front, side and rear, at two distances and in a room where sounds trail off. In Original, try `snd_khz 44` and `22`, and `snd_stereo 1` and `0` on separate starts. Switch between speakers and a headset if available, unplug the headset, then try starting with no playback device and reconnecting it before `/snd_restart`. Note whether sound returns, how long it takes and whether the game stays usable.
- **Saved matches with four players.** Accept the script’s offer when you leave the game menu. Both servers should finish the saved movement, weapon and objective cases. This part takes about 10–20 minutes on its own.
- **More launcher checks.** Try snapping the window, changing display scaling, and moving through Home and Settings with Tab and Narrator. Record what you saw.

### 2. A second PC on the same network · 15–25 minutes

Connect two licensed Windows PCs to a private network disconnected from the internet, with no default gateway. Choose this group on the host and run the same command with `-LanOnly` on the second PC. Host and join each edition from the LAN list and by address. Check the map, player count, version and PunkBuster label. In CoD2x, compare whether the address is visible with `cl_hideServerIp` on and off. Only the local host should answer. If Windows Firewall blocks the test and administrator approval is available, use a temporary rule limited to UDP port 29676 on the private test network and remove it afterwards. Type `quit` in the host’s CoD2 server console when finished.

### 3. A second player · 25–40 minutes

Use the two-PC setup above. Type `P` on the host to play there as well, and join from the other PC.

- **Voice.** In Original set `cl_voice 1` and take turns using push-to-talk to read numbered phrases. Both players should hear clearly and the voice indicator should clear. Try a different microphone and receiving voice with microphone access denied if you can. Note what CoD2x does. Compare against the disc edition on separate spare setups if available.
- **Movement and weapons.** On Toujane try ladders, mantling, landing and falls, crouch, prone, lean, stairs and walls. In Harbor try reaching the water and note changes in movement or damage. On Burgundy fire the pistol, rifle, SMG and shotgun, throw both grenade types and hold the grenade before throwing when the game allows it. Check pickups, kills and respawns. Try a team loadout on Leningrad. Compare playing and spectator views, watch a normal join and note what happens during a short server pause if you can arrange one. Record visible jumps, skipped updates, changes to marks or surfaces and sound differences. Compare normal and competitive CoD2x rules, and repeat the routes in the older game when available. Use `/record kit-lan` and `/stoprecord` to save a local demo. Copy it out of the temporary folder shown in PowerShell before the kit closes. Review it before sharing.
- **Objectives.** On Brecourt **Search & Destroy**, join opposite teams and let a normal round start. Plant and defuse after the opening wait. Look for the prompt, progress, planted sound, score and defender win. On Carentan capture and return a flag, and on Dawnville capture and defend headquarters. Check the on-screen objectives and scores. Compare with the disc edition if available.

### 4. Another keyboard layout · 10–15 minutes

Try a US layout and one other. In AZERTY try Z/W and accents, or in German try Y/Z and typing an accent before a letter. Try AltGr in chat, the physical console key and saved bindings. Repeat the key and focus checks in group 1. Compare the same presses in the disc or older CoD2x game if available. Note any key or typed text that changes unexpectedly.

### 5. A second screen · 10–15 minutes

If possible use screens with different scaling. Move the window from one to the other, restart to check position and use `r_monitor 2` in fullscreen. Try switching away and back in Direct3D 9 and DirectX 7 mode. Change `r_gamma` in SDR, move across screens, switch away and quit. Repeat in HDR if available. The game should use the chosen screen, mouse clicks should line up, and desktop brightness should return.

### 6. A retail disc install · 20–35 minutes

Use your own Call of Duty 2 version 1.3 disc installation with its original program, graphics library and Miles audio library. Allow up to 15 GB free for copies.

- **Compare mouse readings and controls.** The kit checks the original files and compares 4,096 mouse readings. It reports the first difference or says they all match. Use the game menu to compare controls with the disc edition and older CoD2x 1.4.6.8. Repeat the movement between marks and the keyboard, window and sound comparisons above.
- **Repeat the match and start the older server.** Accept the prompts to record the same Toujane match from the disc edition and open the older CoD2x server. It should reach Toujane and answer on this PC. Type `J` to try joining from CoD2x on this PC and note if it worked. Type `quit` in the server window when done. Full starts are skipped if Windows already has a key for the old game. Use a suitable spare PC if you need to run those comparisons.

### 7. A spare Windows account or a lab PC · 25–45 minutes

- **Try other game locations.** Try Steam with CoD2 installed only in a second Steam library. On a lab PC without Steam, test the 32-bit and 64-bit disc install entries separately and together, then try with no valid game files. The 32-bit entry should win when both point to valid installs, and missing game files should get a clear message. Changing the lab entries needs an administrator. Game processes should open as an ordinary user.
- **Try other Windows folders.** On a spare account with a protected disc install, put a distinctive test profile or loose configuration in its matching `VirtualStore` folder before running. On Toujane use /writeconfig kit-saves.cfg, F12, /record kit-saves and /stoprecord. Check the temporary folder shown in PowerShell before the kit closes. All three files should be there, and the old profile should stay hidden. Try a user folder with spaces and accented characters, game and test paths longer than 260 characters with spaces, accents and a `+`, with Windows’ usual long-path setting. Try OneDrive moving Documents and Desktop, or redirected LocalAppData if available. The game should use Windows’ LocalAppData location and leave the disc files, VirtualStore, Documents and Desktop as they were.
- **Install for everyone.** Type `A` at the prompt only on a lab PC with administrator approval. **User Account Control** can ask **Do you want to allow this app from an unknown publisher to make changes to your device?** Approve for this Setup and its uninstall. Setup should use **Program Files**, add shared **Start** entries, and remove them when you confirm uninstall. Note the Windows Security, Defender and signing messages you see.

## If something goes wrong

- Continue after a failed check. Keep the results and note the last thing on screen.
- If a game is stuck behind a Windows prompt, read it and handle it at the laptop.
- If PowerShell or the Mac disconnects, reuse the same run name and folders.
- If cleanup reports a leftover, keep its result and close the test programs
  before a second attempt. Keep existing CoD2 and Steam files where they are.

## Mac transport setup

Prepare a single-run Ed25519 key on the Mac. Give the Windows user only the public line.

```sh
ssh-keygen -t ed25519 -f "$PWD/cod2-kit-key" -N '' -C cod2-laptop-kit
```

For a standard Windows account append that line to
`%USERPROFILE%\.ssh\authorized_keys` using ASCII. For an administrator
account follow Microsoft's instructions for the administrators key file and
ACL. Record the original service startup mode if the interruption check
will require SSH after a restart. Verify the laptop's Ed25519 host fingerprint
on its screen before adding it to the Mac's known hosts.

```powershell
& "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -lf "$env:ProgramData\ssh\ssh_host_ed25519_key.pub"
```

Use the kit on an isolated local link or pass `--online` if the laptop has an
internet route. The fixture retains its loopback endpoints in either case.
The intentionally live Public play check uses a separate run name after
fixture cleanup and Public installation.

```sh
export COD2_KIT_SSH_IDENTITY="$PWD/cod2-kit-key"
export COD2_KIT_SSH_KNOWN_HOSTS="$PWD/cod2-kit-known-hosts"
python3 drive.py LAPTOP_IP WINDOWS_USER --online --kit /path/to/kit.zip --output ./fixture-results
python3 drive.py LAPTOP_IP WINDOWS_USER --online --internet-play --run play --kit /path/to/kit.zip --output ./play-results
```

Add `--cool-down` for the Fixture run on a laptop that needs two idle minutes between groups that start the game or dedicated server. Steps that need a live local match still start a client and its prerequisite server together.

If SSH stops during a restart, sign in again and resume with the same inputs.
Remove only the one-run public key when done. If you installed SSH for this
test, stop it and remove the optional capability when you no longer need it.
If you used the Microsoft MSI, remove that installation through Windows
Apps. Restore any startup mode you changed. [Microsoft public key management](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement)

## Evidence boundaries

The kit checks the Windows build floor and sends the Mac UTC time with each
queued step. The first receipt records OS, PowerShell and monitor DPI.
Registry graphics preferences are read by executable path. The engine's
Direct3D 9 adapter line comes from the ordinal that successfully initialized.
Window evidence includes style, physical frame, monitor bounds and DPI.

The kit keeps `frame-times.bin` with the client evidence and puts a line from
`frame-times.py` in the step receipt. The sample starts after the first map
loads, apart from timedemos, which include loading and use the configured cap.
The full-screen match sample includes the switch away and back. Public play
records the first spectator observation in each edition and uses the cap from
that match's engine summary. The raw trace covers the whole client process and
is written at normal exit. The numbers and any unavailable trace are advisory.
Assembly takes the pinned Python 3.13.17 x64 embeddable ZIP as `--frame-python`.
The analyser runs from the kit folder without installing Python.
Full-screen pictures use the game window at monitor size. Paired pictures
are generated from named game and fixture images. Raw memory dumps stay
outside results.

The installed Fixture and every fixture engine start use loopback endpoints.
The per-user sample credential is in place before automatic discovery starts
any engine. The setup picture run types only the sample key through the
Fixture's temporary credential store. The harness guards the two installed
game import actions at the shared automation boundary. The online transport
uses engine logs to flag non-loopback lookups during fixture runs. These logs
show app-reported activity and complement a process-attributed network trace.

The Public phase uses UI Automation to check the verified-key label and the
installed release status. The CoD2x comparison gets the engine's value in an
offline probe and independently computes the reference from the tagged
CoD2x machine description and retail hash routine. The comparison saves only
its result. [CoD2x 1.4.6.8 reference](https://github.com/callofduty2x/CoD2x/blob/v1.4.6.8/src/mss32/hwid.cpp)

## Launcher desktop receipts

The launcher desktop steps use the installed Test identity and a private kit
profile. They read the current Windows appearance for the dimming result. Leave
Windows appearance, notifications and default apps as they are.

- `launcher-sheet-dimming` saves paired pictures and two pixel samples for
  each of Light, Dark and Match System.
- `launcher-jump-pointer` runs the original pointer sweep.
  `launcher-jump-pointer-repeat` repeats it five times with separate receipts.
- `launcher-tab-names` saves the focused name, type and ID while tabbing from
  the sidebar Settings item and through each section of the Settings window.
- `launcher-activation-running` and `launcher-activation-closed` use only the
  registered Test associations. Their receipts record foreground and monitor.
  A config opens its import review and a server link fills Connect. The steps
  dismiss these sheets and verify that no private files changed or game started.
  A recording plays immediately. The steps check its copy in the private demos
  folder and the playable name passed to the game, close the game, and capture
  Library in the foreground.
- `launcher-taskbar-caption` captures the caption and the Test taskbar button
  when Windows exposes a unique visible button.
- `launcher-update-notice-foreground`, `-minimized` and `-closed` use an
  update offer from a loopback feed. Home shows the offer while foregrounded.
  A background offer can create a Windows notification. The receipt marks
  an attended click when Windows exposes no clickable item. A cold click
  requires a disposable offline Windows account because the notification
  service starts the launcher without the kit's private environment.
