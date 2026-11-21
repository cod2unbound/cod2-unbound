#Requires -Version 5.1
[CmdletBinding()]
param([string]$GamePath, [string]$RetailPath, [switch]$LanOnly)

. (Join-Path $PSScriptRoot 'Common.ps1')
foreach ($script in 'Check-Machine','Check-GameData','Check-Clients','Check-Servers','Check-Replays','Check-Installer') {
    . (Join-Path $PSScriptRoot "$script.ps1")
}

Assert-StandardUser
if ((Read-Host 'Confirm you are using a disposable standard Windows account and the internet is disconnected. Type TEST to begin') -cne 'TEST') {
    throw 'Use a disposable standard account with the internet disconnected.'
}
foreach ($location in @($AppDataRoot, (Join-Path $AppDataRoot 'Windows Kit'), (Join-Path $Kit 'results'))) {
    if ((Test-Path -LiteralPath $location) -and ((Get-Item -LiteralPath $location -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Use a normal folder for both the kit results and the Windows user profile.'
    }
}
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
$Private = New-Folder (Join-Path $AppDataRoot "Windows Kit\$stamp")
$Results = New-Folder (Join-Path $Kit "results\$stamp")
$summary = Join-Path $Results 'automatic-checks.txt'
$hasKey = $false
$hasBaseline = $false

function Invoke-Check([string]$Name, [scriptblock]$Check) {
    try {
        & $Check
        "PASS $Name" | Add-Content -LiteralPath $summary
    } catch {
        "FAIL $Name. $($_.Exception.Message)" | Add-Content -LiteralPath $summary
        Write-Warning "$Name failed. $($_.Exception.Message)"
    }
}

try {
    Test-KitFiles
    Invoke-Check 'Windows and graphics information' { Save-Machine }
    Start-TestSession
    $hasKey = $true
    if ($LanOnly) {
        if ($GamePath) { $GamePath = Get-DataRoot $GamePath }
        else { $GamePath = Read-GamePath '' }
        if ($GamePath) { Invoke-LanSessions $GamePath }
        return
    }

    Write-Host 'Game folders and synthetic key'
    $detected = $null
    try { $detected = Invoke-DataCheck 'discovery-automatic' '' }
    catch { Write-Warning $_.Exception.Message }
    $GamePath = if ($GamePath) { Get-DataRoot $GamePath } else { Read-GamePath $detected.Path }
    if (-not $GamePath) { throw 'A game data folder is required for the remaining checks.' }
    $RetailPath = Get-RetailPath $RetailPath $GamePath
    Invoke-Check 'Initial game file hashes' {
        Save-InstallHashes $GamePath $RetailPath 'before'
    }
    $hasBaseline = Test-Path -LiteralPath (Join-Path $Private 'selected-game-before.txt')
    Invoke-Check 'Chosen, incomplete and linked game folders' { Test-GameData $GamePath }
    Invoke-Check 'Valid and invalid synthetic key' { Invoke-TestKey 'check' }
    if ($RetailPath) { Invoke-Check 'Retail identity and mouse arithmetic' { Test-RetailMouse $RetailPath } }

    Write-Host 'First client runs'
    foreach ($edition in 'original','cod2x') {
        foreach ($renderer in 'dx9','dx7') {
            if (Confirm-Step "Open $edition in $renderer mode") {
                Invoke-Check "$edition $renderer client start and exit" { Invoke-ClientCheck $edition $GamePath $renderer "client-$edition-$renderer" -RequireScreens }
            }
        }
    }
    if (Confirm-Step 'Continue to the mouse, keyboard, window and sound checks') { Invoke-ControlSessions $GamePath $RetailPath }

    Write-Host 'Dedicated servers and saved replays'
    $extracted = $null
    try { $extracted = Expand-ServerPackage }
    catch {
        "FAIL Server ZIP. $($_.Exception.Message)" | Add-Content -LiteralPath $summary
        Write-Warning $_.Exception.Message
    }
    foreach ($edition in 'original','cod2x') {
        if ($extracted) {
            Invoke-Check "$edition server ZIP" { Invoke-ServerCheck $edition $GamePath $extracted "server-$edition" }
        }
        Invoke-Check "$edition standard replay" {
            Invoke-Replay $edition $GamePath 'standing' 'mp_toujane' 'dm' 12345 600 2 (Join-Path $Kit 'replays\standing\usercmds.txt') ''
        }
    }
    if (Confirm-Step 'Run the saved four-player replays') {
        $scenarios = Join-Path $Kit 'replays\scenarios'
        foreach ($line in Get-Content -LiteralPath (Join-Path $scenarios 'cases.tsv')) {
            $name, $mapName, $mode, $seed, $ticks, $players = $line -split '\s+'
            $inputFile = Join-Path $scenarios "$name.txt"
            $scriptFile = Join-Path $scenarios "$name.gsc"
            if (-not (Test-Path -LiteralPath $scriptFile)) { $scriptFile = '' }
            foreach ($edition in 'original','cod2x') {
                Invoke-Check "$edition replay $name" {
                    Invoke-Replay $edition $GamePath $name $mapName $mode $seed $ticks $players $inputFile $scriptFile
                }
            }
        }
    }
    if ($RetailPath -and (Read-Host 'Enter T to capture the same 600-tick replay in retail 1.3, or press Enter to skip') -eq 't') {
        Invoke-Check 'Retail 1.3 native replay capture' { Invoke-RetailReplay $RetailPath $GamePath }
    }
    if ($RetailPath -and (Read-Host 'Enter R to try the source-built CoD2x reference dedicated server, or press Enter to skip') -eq 'r') {
        Invoke-Check 'CoD2x reference startup and queries' {
            Invoke-Classic $RetailPath $GamePath 'reference-dedicated' -Cod2x -Server
        }
    }
    if ((Read-Host 'Enter L for isolated LAN play, or press Enter to skip') -eq 'l') { Invoke-LanSessions $GamePath }

    if ((Read-Host 'Enter I to run the install, upgrade, repair and uninstall checks, or press Enter to skip') -eq 'i') {
        Invoke-Check 'Installer and uninstall' { Invoke-InstallChecks $GamePath }
    }
} finally {
    if ($hasBaseline) {
        try {
            Save-InstallHashes $GamePath $RetailPath 'after' | Set-Content -LiteralPath (Join-Path $Results 'game-files-unchanged.txt')
        } catch { Write-Warning "Could not compare the game folders. $($_.Exception.Message)" }
    }
    if ($hasKey) {
        try { Invoke-TestKey 'clear' } catch { Write-Warning 'The synthetic credential could not be removed. See the notes in START-HERE.' }
    }
    Write-Host "Results folder: $Results"
    Write-Host "Private test profiles and any reference copy are in $Private. Keep them on this computer."
    Write-Host 'Still mark the manual steps in START-HERE: data and account variants, what each renderer showed, input, display, sound and voice, LAN and security prompts.'
    Write-Host 'Mark anything skipped or blocked, add your notes to results, then run Pack-Results.ps1.'
}
