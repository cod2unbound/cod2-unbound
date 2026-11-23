#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Essential')]
param(
    [string]$GamePath,
    [string]$RetailPath,
    [Parameter(ParameterSetName = 'Extras', Mandatory)][switch]$Extras,
    [Parameter(ParameterSetName = 'Lan', Mandatory)][switch]$LanOnly
)

. (Join-Path $PSScriptRoot 'Common.ps1')
foreach ($script in 'Check-Machine','Check-GameData','Check-Clients','Check-Servers','Check-Replays','Check-Launcher','Check-Installer') {
    . (Join-Path $PSScriptRoot "$script.ps1")
}

$groups = @(
    'More checks on this PC'
    'A second PC on the same network'
    'A second player'
    'Another keyboard layout'
    'A second screen'
    'A retail disc install'
    'A spare Windows account or a lab PC'
)

function Show-ExtraGroups {
    for ($i = 0; $i -lt $groups.Count; $i++) {
        Write-Host ('{0}. {1}' -f ($i + 1), $groups[$i])
    }
}

function Invoke-Check([string]$Title, [scriptblock]$Check) {
    try {
        & $Check
        "PASS $Title" | Add-Content -LiteralPath $summary
        Write-Host "PASS - $Title"
    } catch {
        "FAIL $Title. $($_.Exception.Message)" | Add-Content -LiteralPath $summary
        Write-Warning "$Title failed. See automatic-checks.txt in your results folder."
    }
}

function Invoke-ExtraReplays([string]$GamePath) {
    $scenarios = Join-Path $Kit 'replays\scenarios'
    foreach ($line in Get-Content -LiteralPath (Join-Path $scenarios 'cases.tsv')) {
        $name, $mapName, $mode, $seed, $ticks, $players = $line -split '\s+'
        $inputFile = Join-Path $scenarios "$name.txt"
        $scriptFile = Join-Path $scenarios "$name.gsc"
        if (-not (Test-Path -LiteralPath $scriptFile)) { $scriptFile = '' }
        foreach ($edition in 'original','cod2x') {
            Invoke-Check "$(Get-EditionName $edition) four-player replay ($($name.Replace('-', ' ')))" {
                Invoke-Replay $edition $GamePath $name $mapName $mode $seed $ticks $players $inputFile $scriptFile
            }
        }
    }
}

Assert-StandardUser
$group = $null
if ($Extras) {
    Show-ExtraGroups
    $group = Read-Host 'Choose one group by number, or press Enter to quit'
    if (-not $group) { return }
    if ($group -notmatch '^[1-7]$') { throw 'Choose a number from the list.' }
    Write-Host "See Part B in START-HERE.md for $($groups[[int]$group - 1])."
}
$prompt = if ($LanOnly -or $group -in '2','3') { 'Check that both PCs are on the same network with internet access disconnected.' }
    else { 'Close CoD2 and Steam and disconnect from the internet.' }
if ((Read-Host "$prompt Type TEST to begin") -cne 'TEST') { return }
foreach ($location in @($AppDataRoot, (Join-Path $AppDataRoot 'Windows Kit'), (Join-Path $Kit 'results'))) {
    if ((Test-Path -LiteralPath $location) -and ((Get-Item -LiteralPath $location -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Use a kit folder and Windows profile whose test and results folders are ordinary folders.'
    }
}

$Private = $null
$Results = $null
$hasKey = $false
$hasBaseline = $false
$ownsLock = $false
$lock = New-KitLock
try {
    try { $ownsLock = $lock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsLock = $true }
    if (-not $ownsLock) { throw 'A kit check is already open in this Windows account.' }

    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
    $Private = New-Folder (Join-Path $AppDataRoot "Windows Kit\$stamp")
    $Results = New-Folder (Join-Path $Kit "results\$stamp")
    $summary = Join-Path $Results 'automatic-checks.txt'
    $windows = Get-KitWindowsVersion
    "$($windows.Family) $($windows.Release) $($windows.Edition), build $($windows.Build)" |
        Set-Content -LiteralPath $summary

    Test-KitFiles
    Write-Host 'PASS - All kit files copied correctly'
    if (-not ($Extras -or $LanOnly)) { Invoke-Check 'Windows and graphics information saved' { Save-Machine } }
    Assert-NoSavedKitKey
    $hasKey = $true
    Invoke-TestKey 'seed'

    if (-not $GamePath -or -not ($Extras -or $LanOnly)) {
        try { $detected = Invoke-DataCheck 'discovery-automatic' '' }
        catch {
            "FAIL Look for the installed game. $($_.Exception.Message)" | Add-Content -LiteralPath $summary
            Write-Warning 'The game could not finish looking for its files. You can still choose the folder yourself.'
        }
    }
    $GamePath = if ($GamePath) { Get-DataRoot $GamePath } else { Read-GamePath $detected.Path }
    if (-not $GamePath) { throw 'A game folder is required to continue.' }
    if ($Extras -or $LanOnly) {
        Write-Host "Temporary game settings and demos are in $Private until this run ends."
    }

    if ($LanOnly -or $group -in '2','3') {
        Invoke-LanSessions $GamePath
        return
    }

    if (-not $Extras -or $group -in '6','7') {
        if ($group -eq '6') {
            $RetailPath = Get-RetailPath $RetailPath $GamePath
            if (-not $RetailPath) { return }
        } else { $RetailPath = $null }
        Invoke-Check 'Record the game files before testing' { Save-InstallHashes $GamePath $RetailPath 'before' }
        $hasBaseline = Test-Path -LiteralPath (Join-Path $Private 'selected-game-before.txt')
    }

    if ($Extras) {
        switch ($group) {
            '1' {
                Invoke-ControlSessions $GamePath
                if (Confirm-Step 'Run the saved matches with four players') { Invoke-ExtraReplays $GamePath }
            }
            { $_ -in '4','5' } { Invoke-ControlSessions $GamePath }
            '6' {
                Invoke-Check 'Compare mouse readings with the disc edition' { Test-RetailMouse $RetailPath }
                Invoke-ControlSessions $GamePath $RetailPath
                if (Confirm-Step 'Capture the saved Toujane match in the disc edition') {
                    Invoke-Check 'Disc edition Toujane replay' { Invoke-RetailReplay $RetailPath $GamePath }
                }
                if (Confirm-Step 'Start the older CoD2x 1.4.6.8 server') {
                    Invoke-Check 'Older CoD2x server opens and answers on this PC' {
                        Invoke-Classic $RetailPath $GamePath 'reference-dedicated' -Cod2x -Server
                    }
                }
            }
            '7' {
                Invoke-Check 'Check which game folders are accepted' { Test-GameData $GamePath }
                if (Confirm-Step 'Open a game to check where your settings, screenshot and demo are saved') {
                    Invoke-ControlSessions $GamePath
                }
                if ((Read-Host 'Type A to install for all users with administrator approval, or press Enter to skip') -eq 'a') {
                    Invoke-Check 'Install and remove CoD2 Unbound for all users' { Invoke-InstallChecks $GamePath -AllUsers }
                }
            }
        }
        return
    }

    Write-Host 'Game files, test key and servers'
    Invoke-Check 'Check which game folders are accepted' { Test-GameData $GamePath }
    Invoke-Check 'The game reads a valid test key from Windows and rejects a damaged one' { Invoke-TestKey 'check' }
    $extracted = $null
    try { $extracted = Expand-ServerPackage }
    catch {
        "FAIL Server ZIP. $($_.Exception.Message)" | Add-Content -LiteralPath $summary
        Write-Warning 'The server ZIP could not be opened or checked. The saved matches can still run.'
    }
    foreach ($edition in 'original','cod2x') {
        $gameName = Get-EditionName $edition
        if ($extracted) {
            Invoke-Check "$gameName server opens Toujane and answers on this PC" {
                Invoke-ServerCheck $edition $GamePath $extracted "server-$edition"
            }
        }
        Invoke-Check "$gameName server repeats the saved Toujane match" {
            Invoke-Replay $edition $GamePath 'standing' 'mp_toujane' 'dm' 12345 600 2 (Join-Path $Kit 'replays\standing\usercmds.txt') ''
        }
    }

    Write-Host 'Play Toujane in both editions in Direct3D 9'
    foreach ($edition in 'original','cod2x') {
        $label = "$(Get-EditionName $edition), Direct3D 9"
        if (Confirm-Step "Open $label") {
            Invoke-Check "$label opens, saves both pictures and closes" {
                Invoke-ClientCheck $edition $GamePath 'dx9' "client-$edition-dx9" -RequireScreens
            }
        } else { "SKIPPED $label" | Add-Content -LiteralPath $summary }
    }
    if (Confirm-Step 'Install, upgrade, repair and remove CoD2 Unbound for this account') {
        Invoke-Check 'Install, upgrade, repair and remove CoD2 Unbound for this account' { Invoke-InstallChecks $GamePath }
    } else { 'SKIPPED Setup' | Add-Content -LiteralPath $summary }
} finally {
    try {
        if ($hasBaseline) {
            Invoke-Check 'Installed game files and Windows copies stayed the same' {
                $comparison = @(Save-InstallHashes $GamePath $RetailPath 'after')
                $comparison | Set-Content -LiteralPath (Join-Path $Results 'game-files-unchanged.txt')
                if ($comparison -match ': False$') { throw 'Files changed during the check. See game-files-unchanged.txt.' }
            }
        }
        if ($hasKey) {
            try { Invoke-TestKey 'clear' } catch { Write-Warning 'The test key could not be removed. See If something goes wrong in START-HERE.md.' }
        }
        if ($Private) {
            try { Remove-KitScratch $Private }
            catch { Write-Warning "The temporary test folder could not be removed. Close any kit programs still open, then delete $Private." }
        }
    } finally {
        if ($ownsLock) { $lock.ReleaseMutex() }
        $lock.Dispose()
    }
    if ($Results) {
        Write-Host "Results are in $Results"
        Write-Host 'Mark what you saw and anything you skipped in START-HERE.md. Review the results and run Pack-Results.ps1 to make the ZIP.'
        if (-not ($Extras -or $LanOnly)) {
            Write-Host 'The extras can wait for another day. Add -Extras to the same command to choose a group.'
            Show-ExtraGroups
        }
    }
}
