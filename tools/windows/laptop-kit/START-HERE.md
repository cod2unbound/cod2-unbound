# CoD2 Unbound Windows 11 laptop check

This kit checks Original 1.3 and CoD2x for the Windows release. Setup displays version 0.4.0. The 0.3.9 test installer gives Setup an older version to upgrade.

## What ran in the Windows VM

Windows PowerShell 5.1 ran the file and key checks, both loopback servers and the saved matches on a Windows 11 ARM64 VM with x64 emulation and a 640 × 480 software display. The saved matches passed for both editions, including the four-player checks in Part B. Original 1.3 loaded Toujane and saved pictures in both graphics modes. CoD2x stopped before opening a game window because the VM supplied no baseboard information. The laptop still needs both CoD2x runs and the checks for display changes, mouse, keyboard and sound.

VM-only Setup builds passed current-user install, upgrade blocking, upgrade, repair and uninstall. Installed Original 1.3 switched graphics modes after returning to the menu. Run the kit's shipped Setup on the Windows 11 x64 laptop and record any security warning. The VM reported an untrusted root for the test signature.

## Part A — The essential sitting

Allow **45–60 minutes**, including **about 15–20 minutes** for the automatic file, key and server checks. This is an estimate for a laptop with the game already installed. Use one **Windows 11 x64** PC, your **ordinary Windows account**, a licensed Steam or disc installation of Call of Duty 2, and at least 5 GB free. Your usual keyboard, pointer and speakers are enough.

The kit reads the installed game files and saves test settings and screenshots in a temporary folder under LocalAppData. CoD2x keeps this run apart from your saved Windows registry settings. The kit creates a temporary test key in **Credential Manager** and removes it and the temporary folder when it finishes. If you already have a key saved for **CoD2 Unbound**, the kit stops before replacing it. Use an account without a saved Unbound key in that case. The Setup check temporarily adds CoD2 Unbound to **Start** and **Installed apps** for your account, then removes it. If CoD2 Unbound is already installed, the Setup check stops before changing it. The dated results folder stays for you to review.

Copy this whole folder to a writable folder on the PC. In File Explorer, open it, type `powershell` in the address bar and press Enter. Open PowerShell normally.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-All.ps1
```

The programs have a test signature. If **Windows protected your PC** appears, select **More info**, check the file name, then select **Run anyway** if offered. Record a block such as **Smart App Control has blocked an app that may be unsafe** and leave Windows security settings as they are. [Microsoft SmartScreen guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)

Allow about 20–25 minutes for the four portable runs and 10–15 minutes for Setup and packing. Mark each box when it passes. At a script prompt, press Enter to go ahead or type S if it offers a skip. For a failure or skip, write what happened beside the step.

1. [ ] **Start the checks.** Close CoD2 and Steam, disconnect Wi-Fi, Ethernet and VPN, run the command above and type `TEST`.\
   **Expect (automatic)** The kit confirms the copy is complete and records your Windows version, graphics card, driver and Windows signature results.
2. [ ] **Confirm the game folder.** Press Enter to use the folder shown, or paste the Call of Duty 2 folder that contains `main`.\
   **Expect (automatic)** The game accepts a complete folder and saves settings in the test folder. It accepts the valid key from Windows and refuses the damaged key.
3. [ ] **Let both servers run.** Wait while the script opens the servers and repeats the saved Toujane match in each.\
   **Expect (automatic)** Both servers open Toujane, answer on this PC and close. Both saved matches play back as expected.
4. [ ] **Original 1.3 · Direct3D 9.** Follow the play instructions below, including the quick controls and display check.\
   **Expect** You can play Toujane, take both pictures and return to PowerShell.
5. [ ] **Original 1.3 · DirectX 7 mode.** Follow the same play instructions.\
   **Expect** You can play Toujane, take both pictures and return to PowerShell.
6. [ ] **CoD2x · Direct3D 9.** Follow the play instructions, including the quick controls and display check.\
   **Expect** You can play Toujane, take both pictures and return to PowerShell.
7. [ ] **CoD2x · DirectX 7 mode.** Follow the same play instructions.\
   **Expect** You can play Toujane, take both pictures and return to PowerShell.
8. [ ] **Install, upgrade and repair.** In Setup choose **Install for me only** and keep the default folder. Leave the test server open while the kit checks the blocked upgrade.\
   **Expect** Setup installs without asking for administrator approval. The first upgrade attempt leaves the running server alone. The kit then stops that server, upgrades Setup and restores the test file it removes.
9. [ ] **Play the installed games.** Open Toujane in each installed edition. In one, use `/disconnect` to return to the menu, then enter `/set r_rendererPreference dx7` and `/vid_restart`. Open Toujane again and quit each game.\
   **Expect** Both installed games find your game files, and the picture returns after changing graphics mode.
10. [ ] **Uninstall.** Confirm the uninstall prompt, then look for CoD2 Unbound in **Start** and **Installed apps**.\
    **Expect** Its entries are gone. The script confirms uninstall kept the test saves and key, then removes its temporary files and test key.
11. [ ] **Pack the results.** Put your notes in the dated results folder. Review the logs and pictures, then run the packing command below and type `PACK`.\
    **Expect** A `CoD2-Unbound-results-…zip` file appears next to the scripts. Bring back that ZIP.

### Play each portable edition

At the menu, open the game console with the key above Tab and enter `/toggleconsole; screenshotJpeg kit-menu`. Open it again, enter `/devmap mp_toujane`, select **Click to Continue** on the briefing, then pick a team and weapon, and play briefly. Press **F12** while playing to save the second picture. The briefing should be upright and the sky, buildings, weapon and HUD should look right. Check that the graphics options show the mode from the prompt. Use `/quit` to return to PowerShell.

On each Direct3D 9 run, look around quickly, move and release the movement keys, and listen to footsteps and a shot. Aim and movement should feel steady and the sounds should be clear. Alt+Tab to the desktop and back. Use `/disconnect` to return to the menu before applying a different resolution in the graphics options. Reopen Toujane. The desktop should get its pointer back, and the picture and controls should return when you switch back or apply the resolution.

### Pack the results

Include the Windows warnings you saw and a note for any skipped step. Check for names, keys, device IDs and personal paths in the logs and screenshots. Keep licensed game files, private profiles, demos you have yet to review and memory dumps on the laptop.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Pack-Results.ps1
```

## Part B — Extras, when you have them

Each group can be done another day. Record the screen resolution and scaling, keyboard layout and sound devices you use. Run the same command with `-Extras` at the end and choose a group by number. Take notes as you go, restore any Windows settings you change, and pack the extra results the same way.

### 1. More checks on this PC · 35–55 minutes

- **Mouse.** Try Original 1.3 with both mouse options on the script menu and CoD2x with `m_rinput` set to 0, 1 and 2. Move between two marks 10 cm apart slowly and quickly. Try changes to Windows pointer speed and **Enhance pointer precision**. Original’s regular mouse should follow Windows settings, and the direct mouse modes should stay consistent. For CoD2x modes 1 and 2, note `m_rinput_hz` and `m_rinput_hz_max` while moving and again while stopped. Try menus in windowed and fullscreen play, reopen a menu to check pointer position, and try both side buttons and slow and fast wheel turns in both directions if your mouse has them. Note missed releases, extra scroll steps, jumps or drift.
- **Keys and focus.** Try the console key, arrows, **Num Lock**, keypad Enter, left and right modifiers, Backspace held down, and typed and pasted chat. Check saved bindings with `/bindlist`. Switch away while holding a movement key and release it on the desktop. Try the Windows key and Alt+Enter with `developer` set to 0 and 1. Movement should stop when released and saved keys should work after you return.
- **Display recovery.** Change anti-aliasing to an available value, switch between windowed and fullscreen play, Alt+Tab twice and minimise and restore. Use `/disconnect` to return to the menu before applying graphics changes or running `/vid_restart`. Switch graphics modes with `/set r_rendererPreference dx7` and `/vid_restart`, then reopen the map and repeat with `dx9`. **F10** saves an extra picture. Move the window and restart using the same game menu choice to check its restored position. Note `vid_xpos` and `vid_ypos`. Try resizing CoD2x and running in a window the size of the desktop. Watch for misplaced clicks, black screens and a jump on the first mouse movement after returning. For the Task Manager comparison, note CPU usage over ten seconds active and minimised with the same `r_swapInterval` in both games. Repeat with `com_maxfps 85` and `com_maxfps 125`, then restore both settings.
- **Sound devices.** Listen to menu music, footsteps and gunfire from the front, side and rear, at two distances and in a room where sounds trail off. In Original, try `snd_khz 44` and `22`, and `snd_stereo 1` and `0` on separate starts. Switch between speakers and a headset if available, unplug the headset, then try starting with no playback device and reconnecting it before `/snd_restart`. Note whether sound returns, how long it takes and whether the game stays usable.
- **Saved matches with four players.** Accept the script’s offer when you leave the game menu. Both servers should finish the saved movement, weapon and objective cases. This part takes about 10–20 minutes on its own.

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

### Launcher checks · when a launcher build is available

The launcher checks are waiting for a separate build. Its checklist covers finding game files, handling saved keys and imported settings, opening and restarting the game, keyboard and Narrator navigation, links and opening saved files, and updating while a game or server is running.

## If something goes wrong

- **Windows blocks a file.** Note its name, the exact message, Smart App Control’s setting and whether **Run anyway** was offered. A picture of the warning is enough.
- **The game folder or key is rejected.** Check that `main` contains `iw_00.iwd` through `iw_15.iwd`. Note whether you used Steam, disc or a chosen folder. Leave any key prompt blank and keep keys out of notes and screenshots.
- **The game goes black, loses sound or hangs.** Note the edition, graphics mode and last action. Save a picture if you can. Use **Task Manager** to close only the kit game or server that is stuck.
- **Setup or PowerShell stops early.** Keep the results and exact warning. Use **Installed apps** to remove an install you started with this kit. If you know the kit added the entry, remove **CoD2 Unbound/CD Key** in **Credential Manager** under **Windows Credentials**. Delete only that run’s dated folder under `%LOCALAPPDATA%\CoD2 Unbound\Windows Kit` once its programs have closed.
