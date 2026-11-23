# Try CoD2 Unbound on your Windows 11 PC

Use a Windows 11 x64 PC with Call of Duty 2 already installed. The kit uses a
sample CD key and creates a temporary CoD2 Unbound install for your Windows
account. Use a clean Windows account if you already have Unbound or have saved
a key in Windows Credential Manager. Save your work before starting.

## Part A. The laptop sitting

1. Unzip the kit into your own Windows folder. Close CoD2, Steam and apps that
   make sound. Disconnect from the internet. For the Mac route, keep only the
   isolated connection to the Mac. Leave the laptop signed in and unlocked.
2. Open Windows PowerShell in the unzipped folder as your usual user. Start
   the kit with the command below. Omit the game path if the kit can find
   Steam or your disc install. If the file check fails to find the game, run
   again with a new run name and the folder that contains `main`.

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-All.ps1 -GamePath "C:\Games\Call of Duty 2"
   ```

3. The kit checks the copied files, your PC, shader compiler and both servers.
   It asks before running Setup or changing the version. It records the first
   launcher starts, map runs, saved views, sound and input, full-screen
   switching, loopback matches, Hosting, updates and reports where supported.
   It prints a line and saves evidence for each check. If a check fails, carry
   on. Allow an hour for the automatic checks, including update and restart
   time. The Windows VM's unattended run took about 21 minutes with the
   restart and human observations skipped. The laptop may take longer.
4. If **Windows Security** asks about a game or server, choose **Cancel** for
   this offline sitting. Write the message and what you pressed when asked.
   If SmartScreen, Smart App Control or UAC blocks a test, record the wording
   and continue. Leave Windows Security settings as they are.
5. When the paired pictures open, compare the sky, fog, water, lettering and
   world geometry. Write one line about differences that look wrong. In both
   editions, turn slowly and quickly, then listen to gunfire and movement.
   Try **Alt+Tab** away and back in full-screen. Write one line about mouse
   feel, missing or uneven sound, and whether the picture and controls return.
   Allow about 10 minutes.
6. Browse Home and **Settings > Privacy**. Start Narrator with **Windows + Ctrl
   + Enter**. Tab through both pages. Write one line about labels, focus order
   or controls that are hard to reach. Turn Narrator off with the same
   shortcut. Allow about 5 minutes.
7. Give permission to remove the kit's test copy and sample data when asked.
   Read the cleanup result. Inspect the files the packer lists before you
   choose to make the ZIP. Allow 5 minutes for cleanup and review.

To pick up where you stopped, run the same command with the same kit folder
and run name. To start a separate run after cleanup, add `-RunId second`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Pack-Results.ps1 -RunId laptop
```

The packer removes common private identifiers from text. Check the pictures,
demos and sound clips yourself. If Windows wrote a crash dump for the kit's
programs, the packer lists it as raw memory. Review it before sharing.

## Let the Mac drive

Use a standard Windows account and have an administrator set up the optional
Windows OpenSSH Server. Keep a trusted local connection between the two
machines and disconnect that network from the internet. Stay signed in at
the laptop screen during the run.

1. In an administrator PowerShell, run
   `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` before
   disconnecting the internet. This adds Microsoft's SSH server. Windows may
   also create an inbound SSH firewall rule.
2. In the test user's normal PowerShell, save just the Mac's **public** key as
   a new line in `$env:USERPROFILE\.ssh\authorized_keys`. Create the folder
   if you need it. Keep the Mac's private key on the Mac.
3. In administrator PowerShell run `Start-Service sshd`. The Mac must verify
   the SSH host key before the run. Compare the Mac's key fingerprint with
   `ssh-keygen -lf "$env:ProgramData\ssh\ssh_host_ed25519_key.pub"` at the PC.
   Add the matching public host key to the Mac's known hosts. If the run
   includes a restart, run `Set-Service sshd -StartupType Automatic` and sign
   back in when Windows comes up. Keep the laptop on power and awake.
4. On the Mac run the driver from the project checkout. It pauses for consent
   and notes. Handle Windows prompts at the laptop screen.

   ```sh
   tools/windows/laptop-kit/drive.sh LAPTOP_IP WINDOWS_USER \
     --kit /path/to/CoD2-Unbound-Windows-Kit.zip --output /path/to/results
   ```

5. When done, remove the Mac public-key line you added and restore any SSH
   start mode you changed. If you added the service for this test, run
   `Stop-Service sshd` and
   `Remove-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` in an
   administrator PowerShell. Check whether the installer-created
   `OpenSSH-Server-In-TCP` rule remains and remove it if it was new.

If the Mac loses the connection, keep the Windows run folder. Reuse the same
driver command and Mac results folder. To pack already pulled results, run the driver again with
`--pack-only --output /path/to/results`. Use Windows PowerShell for the
checks when the laptop cannot be reached.

[Microsoft OpenSSH setup and removal](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse) ·
[Windows public-key sign-in](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement)

## What ran in the Windows VM

Earlier Windows 11 Arm runs used Microsoft's software graphics with emulated
x64 game programs. The copy with a mainboard ran both editions in both
graphics modes, loaded the four stock maps and took pictures. It also
exercised the shader compiler, full-screen switching, loopback routes,
updating and interrupted-install recovery. The earlier machine without a
mainboard could start only Original 1.3. The current kit ran under key-only
SSH on the copy and pulled a result after every step. Both editions passed
both direct graphics modes, saved-demo comparisons and URI-link joins.
It also ran Windows' own shader compiler, sampled loopback sound and
removed the test install. The Windows software and Wine pictures in this
kit come from earlier runs. Check the picture, mouse feel, sound and
Windows Security prompts on the actual laptop.

## Part B — Extras, when you have them

Each group can be done another day. Record the screen resolution and scaling, keyboard layout and sound devices you use. Run the same command with `-Extras` at the end and choose a group by number. Take notes as you go, restore any Windows settings you change, and pack the extra results the same way.

### 1. More checks on this PC · 35–55 minutes

- **DirectX 7 mode.** Open each edition in DirectX 7 mode from the game menu. Play Toujane and take both pictures using the Part A instructions. Check that the picture returns after switching from Direct3D 9 to DirectX 7 mode and back.
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
