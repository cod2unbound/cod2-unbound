# Test CoD2 Unbound on your Windows laptop

## 1. Get Windows ready

1. Plug in the laptop. Connect it to the internet.
2. Open **Settings > Update & Security > Windows Update**. Install the updates
   Windows offers, restart, and check again until there are no updates left.
   Install a graphics driver only if Windows Update offers it.
3. Check the date, time and time zone after the restart. An incorrect clock can
   make good signatures look invalid. Windows Update also refreshes the
   certificates Windows uses to tell who signed an installer. On a laptop
   that's been off for years, allow an hour or two and expect several restarts.
4. Check **Settings > System > About**. The kit supports Windows 10 22H2 and
   Windows 11 on x64. If Windows is below build 19041, the kit records the
   build and stops before it starts the launcher.

## 2. Choose how to run the kit

1. **Run it yourself.** Unzip the kit in your own folder. Save your work and
   close other apps, including CoD2 and Steam. Disconnect from the internet
   for the local checks. Open Windows PowerShell in the kit directory as your
   usual user and run one command.

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Run-All.ps1
   ```

   If the kit cannot find your game, add
   `-GamePath "C:\Program Files (x86)\Activision\Call of Duty 2"`.
   Use the same command and run name to pick up after an interruption.
   Use a clean Windows account if you already have CoD2 Unbound set up.
2. **Let the Mac drive.** On Windows 10, open Windows PowerShell as
   administrator for these three OpenSSH Server commands. Read and answer
   any Windows approval prompts yourself.

   ```powershell
   Get-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
   Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
   Start-Service sshd
   ```

   If the optional feature still fails after Windows Update, download the
   x64 MSI from Microsoft's Win32-OpenSSH releases. Right-click it, open
   **Properties > Digital Signatures**, select the Microsoft signature, and
   open **Details**. Continue only if Windows says the signature is valid.
   Start the service with the third command if needed. [Microsoft OpenSSH releases](https://github.com/PowerShell/Win32-OpenSSH/releases) · [Microsoft OpenSSH setup](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)
3. Stay signed in and leave the laptop unlocked. Follow the Mac operator's
   directions to set up a one-run SSH key. The Mac can queue checks, get the
   receipts and see pictures of the kit windows. You see the Windows prompts,
   hear the speakers, and judge the mouse and picture. Answer the Windows
   prompts on the laptop, even when the Mac asks you to continue. The local
   checks stay on the laptop if the Mac route needs the laptop to stay
   connected.

## 3. Follow the prompts

1. The first receipt records Windows, PowerShell, display scaling and the
   clock. The kit checks its files, makes a temporary test profile with the
   sample key, and checks the game data and both servers. Allow 10 minutes.
2. Type **YES** when you are ready to install the older test version for your
   account, then again for the newer version. Setup may show **SmartScreen**
   or **Windows Security**, and Windows may ask for **administrator approval**.
   These checks use a lab signature. Windows may label it an unknown
   publisher. Read and answer each message yourself. Cancel an unexpected
   administrator prompt on the per-user route. Write down the words Windows
   showed and your answer. Allow 5 minutes.
3. The first installed server may bring up the **Windows Security firewall**
   question. For the local tests, choose **Cancel**. Write down which program
   asked and what you selected. Windows may ask again for a new program.
   If the kit cannot read the Windows 10 rule list, give it your observation
   when it asks. Allow 5 minutes.
4. The kit opens Setup and the launcher pages and takes pictures. Then both
   game editions run in both graphics modes and join matches on this PC.
   Keep your hands off the keyboard and mouse during those automatic steps.
   Leave the kit's windows in front. Allow 30 to 50 minutes.
5. If Windows trusts the lab signature, the kit asks before each of five
   updates. It asks separately before the update interruption and restart.
   Save your work. After the restart, sign back in as the same user and
   resume. Allow 10 to 20 minutes. If Windows does not trust the lab
   signature, all seven update and restore checks share one skip reason.
6. The kit tests freeze recovery and collects the local report. It then
   shows the comparison pictures and asks what you saw. Give the installer
   permission to remove the test copy and sample key when asked. Read and
   answer any final Windows approval prompt yourself.

## 4. Protect your key and profile

1. **Do not press “Use key from disc install” or any “import from the
   installed game” action during the kit test.** The kit uses its sample key
   and a temporary profile. Your retail key and saved profiles are for
   playing your own game.
2. The kit uses your licensed game archives where they are. It records
   the folder and archive inventory. For a hand comparison, leave your
   retail screenshots, demos and settings out of the results.

## 5. What to check by hand

1. Start from the Windows shortcut and note the icon, title bar, caption
   buttons, dragging, Ctrl+K and solid background on Windows 10.
2. At 100%, 125% and 150% scaling, check Setup, the launcher controls,
   Refresh turning into Stop, and clipping at your usual and narrow sizes.
3. Turn on Narrator and high contrast, tab through Setup, Home, Jump to,
   Servers, Configs, Library and Settings, and write down unclear labels,
   awkward focus order and missing selection marks.
4. Try the keyboard and mouse inputs you use in the bind editor, then try
   the native file picker, folder reveal, Recycle Bin undo and Share setup
   overflow with sample files and the local fixtures.
5. Right-click the taskbar icon and try the three tasks and recent destinations
   from a closed and open launcher, then try clearing recents and record what
   the jump list shows.
6. With a trusted update fixture, check the notification banner and activation
   from Notification Center, including when the launcher is in front, when a
   game or room is running, and when Windows notifications are off.
7. Use your own working retail shortcut to compare the picture, mouse feel in
   menus and in play, and sound on the speakers with both Unbound editions.
8. On each graphics chip, run a local match in exclusive full screen at the
   desktop and a lower mode, Alt+Tab away and back, and write down whether
   the picture, sound and movement return.
9. If a receipt says Intel, look in **Settings > System > Display > Graphics
   settings**, or in AMD's switchable graphics page, and compare labelled
   Intel and Radeon runs if Windows offers both choices. The kit only reads
   the choice. To label your run, add `-GraphicsLabel Intel` or
   `-GraphicsLabel Radeon`, or have the Mac use the corresponding
   `--graphics-label`. A label records your choice. The engine receipt
   records what it used.
10. Try Return to game with the launcher minimize setting off and on,
    cancel the close question once, then close the game and launcher
    together and note which windows return or stop.
11. On a second display if available, check the chosen monitor and what
    happens when it is removed. Check physical speaker and microphone
    behavior separately from the kit's loopback sound.
12. With a disposable account and test loadout, exercise the Original
    hardware and changed-config questions, write down their wording and
    outcome, and try both answers.
13. On an isolated two-PC network, record whether the hosted game remains
    reachable after Cancel and ends after Stop and quit. Repeat the
    interactive Setup and uninstall questions when a local server is running.
14. With a trusted test installer, repeat the update interruption once
    after saving your work, sign back in and record the restore and restart
    behavior.
15. When publisher signing is available, repeat per-user Setup, Start,
    upgrade and uninstall and record the SmartScreen, Defender and
    Windows Security wording and your choices.

## 6. Send back the results

1. If you ran it yourself, run
   `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Pack-Results.ps1 -RunId laptop`
   from the kit folder. Review the listed results and type **PACK**. The ZIP
   appears next to the kit scripts. For a Mac-driven run the ZIP is made in
   the Mac results folder.
2. The archive uses the sample profile, crops window pictures and scrubs
   common private identifiers. Real keys and retail profiles should stay
   on your PC. Review the pictures, sound and notes before you send the ZIP
   back to the Mac operator, and share it privately.

## 7. Play a real match

1. After the Fixture run is removed, install the separate **Public** play
   build. Windows may show an unknown publisher for its lab signature.
   Read and answer its prompt yourself. In this build's Setup use **Use key
   from disc install** on purpose and check that Settings reports a verified
   key. Keep reports and usage sharing off for the play check.
2. If the Mac runs the optional online check, it asks once before using the
   verified key through the product. It uses real game servers and briefly
   lists test rooms. It stops at a refusal, kick, ban, checksum, version or
   anti-cheat message. Record the exact words. The public server names and
   addresses stay in the private results.
3. Play one real match on Original 1.3. Play one on CoD2x after the same-PC
   identifier check agrees with CoD2x 1.4.6.8. Compare the picture with what
   you remember. Try aiming and listening on the speakers. Watch hit
   registration, the lag meter and any stutter. Write a line for each edition
   in your private results notes.
