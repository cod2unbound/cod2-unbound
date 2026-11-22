function Read-InstalledVersion([string]$Scope, [string]$Root) {
    $key = "${Scope}:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1"
    if (-not (Test-Path -LiteralPath $key)) { return }
    $registered = (Get-ItemProperty -LiteralPath $key).DisplayVersion
    $version = (Get-Content -LiteralPath (Join-Path $Root 'RELEASE_VERSION') -Raw).Trim()
    if ($registered -ne $version) { throw 'The installer and its Windows registration disagree about the version.' }
    return $version
}

function Invoke-Setup([string]$Exe, [string]$Label, [string]$Options = '') {
    Assert-Offline
    $detail = New-RunContext $Label
    $rawLog = Join-Path $detail.Folder 'setup.log'
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList "$Options /LOG=`"$rawLog`"" -PassThru -Wait
        try { return $p.ExitCode } finally { $p.Dispose() }
    } finally { Save-Text $rawLog (Join-Path $detail.Evidence 'setup.log') }
}

function Assert-InstalledFiles([string]$Root, [string]$From) {
    foreach ($edition in 'original','cod2x') {
        foreach ($role in 'cod2_win32.exe','cod2_win32_ded.exe') {
            $rel = Join-Path $edition $role
            if ((Get-FileHash -LiteralPath (Join-Path $Root $rel)).Hash -ine (Get-FileHash -LiteralPath (Join-Path $From $rel)).Hash) {
                throw "Installed file $rel differs from the portable file."
            }
        }
    }
}

function New-PreservationFiles {
    $data = New-Folder (Join-Path $Private 'uninstall-preservation')
    foreach ($name in 'main\players\kit\config_mp.cfg','main\screenshots\kit-preserve.jpg','main\demos\kit-preserve.dm_1') {
        $full = Join-Path $data $name
        New-Folder (Split-Path $full -Parent) | Out-Null
        "Synthetic user file $name" | Set-Content -LiteralPath $full -Encoding ASCII
    }
    return $data
}

function Stop-SetupServer([string]$Password, [int]$Port) {
    $socket = [Net.Sockets.UdpClient]::new()
    try {
        $socket.Connect([Net.IPAddress]::Loopback, $Port)
        [byte[]]$packet = @(255, 255, 255, 255) + [Text.Encoding]::ASCII.GetBytes("rcon $Password quit")
        [void]$socket.Send($packet, $packet.Length)
    } finally { $socket.Dispose() }
}

function Invoke-InstallChecks([string]$GamePath, [switch]$AllUsers) {
    $install = Join-Path $Kit 'install'
    $setup = Get-ChildItem -LiteralPath $install -File -Filter 'CoD2-Unbound-*-setup.exe'
    $prior = Get-ChildItem -LiteralPath (Join-Path $install 'previous') -File -Filter 'CoD2-Unbound-*-setup.exe'
    $root = Join-Path $LocalData 'Programs\CoD2 Unbound'
    $allRoot = Join-Path $env:ProgramFiles 'CoD2 Unbound'
    $uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1'
    $start = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'CoD2 Unbound'
    $commonStart = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)) 'CoD2 Unbound'
    if ((Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $allRoot) -or
        (Test-Path -LiteralPath "HKCU:\$uninstallKey") -or (Test-Path -LiteralPath "HKLM:\$uninstallKey") -or
        (Test-Path -LiteralPath $start) -or (Test-Path -LiteralPath $commonStart)) {
        throw 'Setup checks require a clean install on this PC. Leave any existing install in place and skip this section.'
    }

    $oldVersion = $prior.Name.Split('-')[2]
    $newVersion = $setup.Name.Split('-')[2]
    $quiet = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER'
    $profile = New-PreservationFiles
    $before = (Get-TreeHashes $profile) -join "`n"
    $registry = @(Find-RetailPaths) | ConvertTo-Json
    $installed = $false
    $allInstalled = $false
    $launcherHome = $null
    $launcherBefore = $null
    try {
        if ($AllUsers) {
            Write-Host 'Approve the User Account Control prompt for this test and its uninstall. Keep the default install folder.'
            $allExit = Invoke-Setup $setup.FullName 'setup-all-users' '/ALLUSERS'
            if ($allExit -ne 0) { throw 'Setup for all users was cancelled, blocked or failed.' }
            $allInstalled = $true
            if ((Read-InstalledVersion 'HKLM' $allRoot) -ne $newVersion -or
                @(Get-ChildItem -LiteralPath $commonStart -File -Filter '*.lnk').Count -ne 2) {
                throw 'The install for all users did not appear in Installed apps or Start as expected.'
            }
            Assert-InstalledFiles $allRoot (Join-Path $Kit 'portable')
            return
        }

        Write-Host 'In Setup choose Install for me only and keep the default folder. Note any Windows warning or permission prompt.'
        if ((Invoke-Setup $prior.FullName 'setup-previous') -ne 0) { throw 'The previous installer was cancelled or blocked.' }
        $installed = $true
        if ((Read-InstalledVersion 'HKCU' $root) -ne $oldVersion -or
            @(Get-ChildItem -LiteralPath $start -File -Filter '*.lnk').Count -ne 3) {
            throw 'The previous installer did not create the per-user install and Start entries.'
        }
        $entry = New-RunContext 'setup-busy-server'
        $homePath = New-Folder (Join-Path $entry.Folder 'game')
        $server = Get-Binary 'original' -Server -Root $root
        $oldHash = (Get-FileHash -LiteralPath $server).Hash
        $port = 29676
        $stopPassword = [guid]::NewGuid().ToString('N').Substring(0,20)
        $arguments = (Get-EngineArgs $homePath $GamePath -Address '127.0.0.1' -Port $port) +
            " +set rcon_password $stopPassword +set dedicated 1 +set g_gametype dm +map mp_toujane"
        $running = Start-KitProcess $server $arguments $entry.Folder
        try {
            Save-ServerQueries $running.Process $entry.Evidence $port '118'
            $busyExit = Invoke-Setup $setup.FullName 'setup-busy-upgrade' $quiet
            "Setup exit code $busyExit" | Set-Content -LiteralPath (Join-Path $entry.Evidence 'busy-exit.txt')
            if ($busyExit -eq 0 -or $running.Process.HasExited -or (Read-InstalledVersion 'HKCU' $root) -ne $oldVersion -or
                (Get-FileHash -LiteralPath $server).Hash -ne $oldHash) { throw 'Setup changed the old install while the server was still running.' }
            Write-Host 'Setup left the old install alone while the server was running.'
        } finally {
            try {
                if (-not $running.Process.HasExited) { Stop-SetupServer $stopPassword $port }
            } finally {
                $stopped = Complete-KitProcess $running $entry.Evidence 75
                Save-GameEvidence $homePath $entry.Evidence
            }
        }
        if ($stopped.TimedOut -or $stopped.ExitCode -ne 0) { throw 'The installed server did not close cleanly.' }
        if ((Invoke-Setup $setup.FullName 'setup-upgrade' $quiet) -ne 0 -or (Read-InstalledVersion 'HKCU' $root) -ne $newVersion) {
            throw 'The upgrade failed.'
        }
        $uninstallers = @(Get-ChildItem -LiteralPath $root -File -Filter 'unins*.exe')
        if ($uninstallers.Count -ne 1) { throw 'Expected one uninstaller after upgrade.' }
        Save-Signatures $uninstallers.FullName (Join-Path $Results 'installed-uninstaller-signature.json')
        Save-Signatures @(
            (Join-Path $root 'launcher\CoD2Unbound.Windows.exe')
            (Join-Path $root 'payload.cat')
        ) (Join-Path $Results 'installed-launcher-signatures.json')
        $repair = Join-Path $root 'launcher\CoD2Unbound.Windows.pri'
        $repairHash = (Get-FileHash -LiteralPath $repair).Hash
        Remove-Item -LiteralPath $repair
        if ((Invoke-Setup $setup.FullName 'setup-repair' $quiet) -ne 0 -or
            (Get-FileHash -LiteralPath $repair).Hash -ne $repairHash) { throw 'Repair did not restore the launcher file.' }
        Assert-InstalledFiles $root (Join-Path $Kit 'portable')

        Write-Host 'The newer version is installed and Setup restored the removed test file.'
        if (Confirm-Step 'Check the launcher from Start and save pictures') {
            Invoke-Check 'Installed Start shortcut, first-run Setup and launcher pictures' {
                Invoke-LauncherCheck $root $start $GamePath
            }
            $launcherHome = Join-Path $Private 'launcher\game'
            $launcherBefore = (Get-TreeHashes $launcherHome) -join "`n"
        } else {
            'SKIPPED launcher' | Add-Content -LiteralPath $summary
        }
    } finally {
        $uninstallFailed = $false
        if ($installed -or $allInstalled) {
            Write-Host 'Confirm the uninstall prompt. Then check Installed apps and Start for CoD2 Unbound.'
            foreach ($scope in @(
                @{ Present = $allInstalled; Directory = $allRoot; Group = $commonStart; Key = "HKLM:\$uninstallKey"; Label = 'uninstall-all-users' },
                @{ Present = $installed; Directory = $root; Group = $start; Key = "HKCU:\$uninstallKey"; Label = 'uninstall-current-user' }
            )) {
                if (-not $scope.Present) { continue }
                $uninstaller = Get-ChildItem -LiteralPath $scope.Directory -File -Filter 'unins*.exe' | Select-Object -First 1
                if (-not $uninstaller -or (Invoke-Setup $uninstaller.FullName $scope.Label) -ne 0 -or
                    (Test-Path -LiteralPath $scope.Key) -or (Test-Path -LiteralPath $scope.Group) -or
                    @(Get-ChildItem -LiteralPath $scope.Directory -Recurse -File -ErrorAction SilentlyContinue).Count) {
                    $uninstallFailed = $true
                    Write-Warning "Check $($scope.Label) in results. Windows may have left some installed files or shortcuts."
                } else {
                    "PASS $($scope.Label) removed the installed files and registration." |
                        Add-Content -LiteralPath (Join-Path $Results 'setup-summary.txt')
                }
            }
        }
        Invoke-TestKey 'verify'
        $after = (Get-TreeHashes $profile) -join "`n"
        if ($before -ne $after -or $registry -ne (@(Find-RetailPaths) | ConvertTo-Json)) {
            throw 'A preservation file or retail InstallPath changed during the installer checks.'
        }
        if ($launcherHome -and $launcherBefore -ne ((Get-TreeHashes $launcherHome) -join "`n")) {
            throw 'Uninstall changed the launcher test loadout or settings.'
        }
        'Uninstall kept the test settings, screenshot, demo and key, and the disc game location.' |
            Add-Content -LiteralPath (Join-Path $Results 'setup-summary.txt')
        if ($uninstallFailed) { throw 'An uninstall check failed. See setup-summary and the setup logs.' }
    }
}
