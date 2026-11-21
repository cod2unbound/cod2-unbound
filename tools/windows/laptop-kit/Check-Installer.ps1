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

function Invoke-InstallChecks([string]$GamePath) {
    $install = Join-Path $Kit 'install'
    $setup = Get-ChildItem -LiteralPath $install -File -Filter '*-setup.exe'
    $prior = Get-ChildItem -LiteralPath (Join-Path $install 'previous') -File -Filter '*-setup.exe'
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
    try {
        Write-Host 'In Setup choose Install for me only. Record the warning, any UAC prompt and the Start entries.'
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
        $arguments = (Get-EngineArgs $homePath $GamePath) + ' +set dedicated 1 +set g_gametype dm +map mp_toujane'
        $running = Start-KitProcess $server $arguments $entry.Folder
        try {
            Save-ServerQueries $running.Process $entry.Evidence 29676 '118'
            $busyExit = Invoke-Setup $setup.FullName 'setup-busy-upgrade' $quiet
            "Setup exit code $busyExit" | Set-Content -LiteralPath (Join-Path $entry.Evidence 'busy-exit.txt')
            if ($busyExit -eq 0 -or $running.Process.HasExited -or (Read-InstalledVersion 'HKCU' $root) -ne $oldVersion -or
                (Get-FileHash -LiteralPath $server).Hash -ne $oldHash) { throw 'Busy setup failed to preserve the running installation.' }
        } finally {
            if (-not $running.Process.HasExited) {
                Write-Host 'Type quit in the CoD2 server console. Setup waits for it to stop.'
            }
            $stopped = Complete-KitProcess $running $entry.Evidence 75
            Save-GameEvidence $homePath $entry.Evidence
        }
        if ($stopped.TimedOut -or $stopped.ExitCode -ne 0) { throw 'The installed server did not close cleanly.' }
        if ((Invoke-Setup $setup.FullName 'setup-upgrade' $quiet) -ne 0 -or (Read-InstalledVersion 'HKCU' $root) -ne $newVersion) {
            throw 'The upgrade failed.'
        }
        $uninstallers = @(Get-ChildItem -LiteralPath $root -File -Filter 'unins*.exe')
        if ($uninstallers.Count -ne 1) { throw 'Expected one uninstaller after upgrade.' }
        Save-Signatures $uninstallers.FullName (Join-Path $Results 'installed-uninstaller-signature.json')
        $repair = Get-Binary 'cod2x' -Server -Root $root
        Remove-Item -LiteralPath $repair
        if ((Invoke-Setup $setup.FullName 'setup-repair' $quiet) -ne 0) { throw 'Repair failed.' }
        Assert-InstalledFiles $root (Join-Path $Kit 'portable')

        if (Confirm-Step 'Try both installed clients now') {
            foreach ($edition in 'original','cod2x') {
                try { Invoke-ClientCheck $edition $GamePath -Label "installed-$edition" -BinaryRoot $root }
                catch { Write-Warning $_.Exception.Message }
            }
        }
        if ((Read-Host 'Enter A for the optional all-users test (requires administrator approval), or press Enter to skip') -eq 'a') {
            $allExit = Invoke-Setup $setup.FullName 'setup-all-users' '/ALLUSERS'
            if ($allExit -eq 0) {
                $allInstalled = $true
                if ((Read-InstalledVersion 'HKLM' $allRoot) -ne $newVersion -or
                    @(Get-ChildItem -LiteralPath $commonStart -File -Filter '*.lnk').Count -ne 3) {
                    throw 'The all-users registration or Start entries were missing.'
                }
                Assert-InstalledFiles $allRoot (Join-Path $Kit 'portable')
            } else { Write-Warning "All-users setup ended with exit code $allExit." }
        }
    } finally {
        $uninstallFailed = $false
        if ($installed -or $allInstalled) {
            Write-Host 'Uninstall is last. Record any elevation request and confirm the uninstall prompt.'
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
        'Synthetic config, screenshot, demo, CD key and retail InstallPath preserved.' |
            Add-Content -LiteralPath (Join-Path $Results 'setup-summary.txt')
        if ($uninstallFailed) { throw 'An uninstall check failed. See setup-summary and the setup logs.' }
    }
}
