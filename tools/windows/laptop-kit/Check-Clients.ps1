function New-TestProfile([string]$HomePath, [switch]$Classic) {
    $profile = New-Folder (Join-Path $HomePath 'main\players\kit')
    'kit' | Set-Content -LiteralPath (Join-Path (Split-Path $profile -Parent) 'active.txt') -Encoding ASCII
    @('bind F10 "screenshotJpeg kit-extra"', 'bind F11 "screenshotJpeg kit-menu"',
        'bind F12 "screenshotJpeg kit-toujane"', 'seta name "Kit Player"') |
        Set-Content -LiteralPath (Join-Path $profile 'config_mp.cfg') -Encoding ASCII
    if ($Classic) {
        '000000000000000086D3' | Set-Content -LiteralPath (Join-Path $HomePath 'main\cod2key') -Encoding ASCII
    }
}

function Invoke-ClientCheck {
    param([string]$Edition, [string]$GamePath, [string]$Renderer = 'dx9', [string]$Label,
        [string]$BinaryRoot = (Join-Path $Kit 'portable'), [switch]$Controls, [string]$JoinAddress, [switch]$Raw, [switch]$Lan, [string]$SavedProfile, [switch]$RequireScreens)
    $entry = New-RunContext $Label
    $homePath = if ($SavedProfile) { $SavedProfile } else { Join-Path $entry.Folder 'game' }
    $firstStart = -not (Test-Path -LiteralPath $homePath)
    if ($firstStart) { New-TestProfile $homePath }
    $address = if ($Lan -or $JoinAddress) { '0.0.0.0' } else { '127.0.0.1' }
    $launchArgs = (Get-EngineArgs $homePath $GamePath -Address $address -Port 29677) +
        " +set r_rendererPreference $Renderer +set g_gametype dm +set cl_allowDownload 0 +set cl_punkbuster 0"
    if ($firstStart) { $launchArgs += ' +set r_fullscreen 0 +set r_mode 800x600 +set r_aaSamples 1' }
    if ($JoinAddress -or $Lan) { $launchArgs += ' +set cl_shownet 2' }
    else { $launchArgs += ' +set net_noudp 1' }
    if ($JoinAddress) { $launchArgs += " +connect $JoinAddress" }
    if ($Controls -and $firstStart) {
        $launchArgs += ' +set sensitivity 5 +set m_pitch 0.022 +set m_yaw 0.022 +set m_filter 0 +set cl_mouseAccel 0'
    }
    if ($Raw) { $launchArgs += ' +set in_rawmouse 1' }
    Write-Host "$(Get-EditionName $Edition), $(Get-GraphicsMode $Renderer). Use /quit to return to this window."
    if ($RequireScreens) {
        Write-Host 'Open the console with the key above Tab and enter /toggleconsole; screenshotJpeg kit-menu'
        Write-Host 'Reopen the console, enter /devmap mp_toujane, select Click to Continue, then pick a team and weapon. Play briefly and press F12.'
        if ($Renderer -eq 'dx9') {
            Write-Host 'Try the mouse, keyboard and sound. Alt+Tab out and back. Use /disconnect to return to the menu before changing the game resolution.'
        }
    }
    $proc = Start-KitProcess (Get-Binary $Edition -Root $BinaryRoot) $launchArgs $entry.Folder
    $done = Complete-KitProcess $proc $entry.Evidence 1200
    Save-GameEvidence $homePath $entry.Evidence
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw "The client did not exit cleanly. See $Label in results." }
    if ($RequireScreens) {
        $log = Join-Path $entry.Evidence 'console_mp.log'
        $pathMessage = 'Using Direct3D {0} code path' -f $Renderer.Substring(2)
        if (-not (Test-Path -LiteralPath $log) -or -not (Select-String -LiteralPath $log -SimpleMatch $pathMessage -Quiet)) {
            throw "The game did not report $(Get-GraphicsMode $Renderer). See its results."
        }
        foreach ($capture in 'kit-menu.jpg','kit-toujane.jpg') {
            if (-not (Test-Path -LiteralPath (Join-Path $entry.Evidence $capture))) { throw "The game did not save $capture." }
        }
    }
}

function Test-ClassicKeyIsAbsent {
    $retailKey = 'SOFTWARE\Activision\Call of Duty 2'
    foreach ($view in 'Registry32','Registry64') {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view)
        try {
            $key = $baseKey.OpenSubKey($retailKey)
            if ($key) {
                try { if ($key.GetValueNames() -contains 'codkey') { return $false } }
                finally { $key.Dispose() }
            }
        } finally { $baseKey.Dispose() }
    }
    foreach ($path in @("HKCU:\$retailKey",
        "HKCU:\Software\Classes\VirtualStore\MACHINE\SOFTWARE\$($retailKey.Substring(9))",
        "HKCU:\Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\$($retailKey.Substring(9))")) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).GetValueNames() -contains 'codkey')) { return $false }
    }
    return $true
}

function New-ClassicCopy([string]$RetailPath, [string]$GamePath, [string]$Root, [switch]$Cod2x) {
    if (-not (Test-ClassicKeyIsAbsent)) {
        throw 'Windows has a saved key for the old game. This comparison was skipped to protect it.'
    }
    $main = New-Folder (Join-Path $Root 'main')
    foreach ($name in 'CoD2MP_s.exe','gfx_d3d_mp_x86_s.dll','mss32.dll') {
        Copy-Item -LiteralPath (Join-Path $RetailPath $name) -Destination $Root
    }
    foreach ($name in 'localization.txt','codlogo.bmp','cod.bmp') {
        $source = Join-Path $GamePath $name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $Root }
    }
    # Copy only licensed archives, since the retail engine may write alongside them.
    Get-ChildItem -LiteralPath (Join-Path $GamePath 'main') -File -Filter '*.iwd' |
        Copy-Item -Destination $main
    $miles = Join-Path $RetailPath 'miles'
    if (Test-Path -LiteralPath $miles) { Copy-Item -LiteralPath $miles -Destination $Root -Recurse }
    if ($Cod2x) {
        Rename-Item -LiteralPath (Join-Path $Root 'mss32.dll') -NewName 'mss32_original.dll'
        Copy-Item -LiteralPath (Join-Path $Kit 'probes\reference\mss32.tag.dll') -Destination (Join-Path $Root 'mss32.dll')
    }
    New-TestProfile $Root -Classic
}

function Invoke-Classic([string]$RetailPath, [string]$GamePath, [string]$Label, [switch]$Cod2x, [switch]$Server) {
    $entry = New-RunContext $Label
    $folderName = if ($Cod2x) { 'reference-CoD2x-copy' } else { 'reference-retail-copy' }
    $copy = Join-Path $Private $folderName
    if (-not (Test-Path -LiteralPath (Join-Path $copy 'main\cod2key'))) {
        New-ClassicCopy $RetailPath $GamePath (New-Folder $copy) -Cod2x:$Cod2x
    }
    $arguments = (Get-EngineArgs $copy $copy) +
        ' +set fs_cdpath "" +set sv_pure 0 +set g_gametype dm +set cl_allowDownload 0'
    if ($Server) {
        Write-Host 'The older CoD2x server will open and answer on this PC.'
        $arguments += ' +set dedicated 1 +map mp_toujane +dvarlist'
    } else {
        $arguments += ' +set net_noudp 1 +set r_fullscreen 0 +set sensitivity 5 +set m_filter 0 +set cl_mouseAccel 0'
        Write-Host 'Try the controls in this separate copy of the older game. Use /quit when finished.'
    }
    $run = Start-KitProcess (Join-Path $copy 'CoD2MP_s.exe') $arguments $copy
    try {
        if ($Server) {
            Save-ServerQueries $run.Process $entry.Evidence 29676 '120'
            if ((Read-Host 'Type J to join the older server from CoD2x, or press Enter to skip') -eq 'j') {
                try {
                    Invoke-ClientCheck 'cod2x' $GamePath -Label 'reference-server-join' -JoinAddress '127.0.0.1:29676'
                } catch { Write-Warning $_.Exception.Message }
            }
            Read-Host 'Type quit in the server console, then press Enter here' | Out-Null
        }
    } finally {
        $done = Complete-KitProcess $run $entry.Evidence 1200
        Save-GameEvidence $copy $entry.Evidence
    }
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw 'The older game did not close normally. See its results.' }
}

function Invoke-ControlSessions([string]$GamePath, [string]$RetailPath) {
    $number = 0
    do {
        Write-Host '1 Original 1.3    2 Original 1.3 with direct mouse input    3 CoD2x'
        if ($RetailPath) { Write-Host '4 Disc edition    5 Older CoD2x 1.4.6.8' }
        $selection = Read-Host 'Choose a game by number, or press Enter to finish this group'
        if (-not $selection) { break }
        $number++
        try {
            switch ($selection) {
                '1' { Invoke-ClientCheck 'original' $GamePath -Label "controls-$number-original" -Controls -SavedProfile (Join-Path $Private "controls-original-profile") }
                '2' { Invoke-ClientCheck 'original' $GamePath -Label "controls-$number-original-raw" -Controls -Raw -SavedProfile (Join-Path $Private "controls-original-raw-profile") }
                '3' { Invoke-ClientCheck 'cod2x' $GamePath -Label "controls-$number-cod2x" -Controls -SavedProfile (Join-Path $Private "controls-cod2x-profile") }
                '4' { if ($RetailPath) { Invoke-Classic $RetailPath $GamePath "controls-$number-retail" } }
                '5' { if ($RetailPath) { Invoke-Classic $RetailPath $GamePath "controls-$number-reference" -Cod2x } }
            }
        } catch { Write-Warning $_.Exception.Message }
    } while ($true)
}
