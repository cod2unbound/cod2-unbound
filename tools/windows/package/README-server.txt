CoD2 Unbound dedicated server for Windows 11 x64
================================================

1. Verify the downloaded ZIP, then extract it to a folder you manage. Keep the
   extracted binaries separate from your licensed Call of Duty 2 installation
   and from your writable server files.
2. Make a folder under "%LOCALAPPDATA%\CoD2 Unbound\servers" for each instance.
   Within it create "main" and copy the supplied server.cfg there. Edit that
   copy to change the hostname, rotation or other settings.
3. Open Command Prompt in the extracted folder. Replace the example paths
   with your game-data directory and writable instance path.

   original\cod2_win32_ded.exe +set fs_basepath "D:/Games/Call of Duty 2" +set fs_homepath "C:/Users/YourName/AppData/Local/CoD2 Unbound/servers/lan" +set dedicated 1 +set net_port 28960 +exec server.cfg +map_rotate

   For CoD2x (1.4.6.8), replace original with cod2x. The game-data directory
   holds main\iw_00.iwd through main\iw_15.iwd and the localization archives.
   Use forward slashes inside both quoted path arguments.

4. UDP 28960 is conventional. Give simultaneous instances distinct ports. For
   LAN access allow inbound UDP on the selected port for the chosen executable
   in Windows Defender Firewall, scoped to the needed network and clients.
   A firewall rule may require an administrator. Use dedicated 2 and configure
   master servers and router forwarding only if you intend public hosting.
5. Type quit in the server console to stop. Instance configs, mods, downloads
   and logs are under the chosen writable directory. Keep a private RCON
   password in your config if you enable RCON. The default leaves RCON off.

Both editions can use the CoD2 Unbound CD Key credential for the same Windows
user. Keep it out of command lines and server config files. Keep all licensed
game data in the original game installation.

To update, stop the instance, verify the new download and extract its files
to a new folder. Start it with the same game and instance arguments. Keep
the old binaries until you have checked the new build.
