function New-TestProfile([string]$HomePath, [switch]$Classic) {
    $profile = New-Folder (Join-Path $HomePath 'main\players\kit')
    'kit' | Set-Content -LiteralPath (Join-Path (Split-Path $profile -Parent) 'active.txt') -Encoding ASCII
    @('bind F10 "screenshotJpeg kit-extra"', 'bind F11 "screenshotJpeg kit-menu"',
        'bind F12 "screenshotJpeg kit-toujane"') |
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
    Write-Host "$Edition, $Renderer. Use /quit to return to this window."
    $proc = Start-KitProcess (Get-Binary $Edition -Root $BinaryRoot) $launchArgs $entry.Folder
    $done = Complete-KitProcess $proc $entry.Evidence 1200
    Save-GameEvidence $homePath $entry.Evidence
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw "The client did not exit cleanly. See $Label in results." }
    if ($RequireScreens) {
        $log = Join-Path $entry.Evidence 'console_mp.log'
        $pathMessage = 'Using Direct3D {0} code path' -f $Renderer.Substring(2)
        if (-not (Test-Path -LiteralPath $log) -or -not (Select-String -LiteralPath $log -SimpleMatch $pathMessage -Quiet)) {
            throw "$Label did not report the requested renderer. See its logs."
        }
        foreach ($capture in 'kit-menu.jpg','kit-toujane.jpg') {
            if (-not (Test-Path -LiteralPath (Join-Path $entry.Evidence $capture))) { throw "$Label is missing $capture." }
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
        throw 'A legacy CD key is registered on this PC. The reference game is skipped to protect that key.'
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
        Write-Host 'The reference server will be queried on loopback. Close its console or type quit when prompted.'
        $arguments += ' +set dedicated 1 +map mp_toujane +dvarlist'
    } else {
        $arguments += ' +set net_noudp 1 +set r_fullscreen 0 +set sensitivity 5 +set m_filter 0 +set cl_mouseAccel 0'
        Write-Host 'Use the copied reference game for the control comparison. Use /quit when finished.'
    }
    $run = Start-KitProcess (Join-Path $copy 'CoD2MP_s.exe') $arguments $copy
    try {
        if ($Server) {
            Save-ServerQueries $run.Process $entry.Evidence 29676 '120'
            Read-Host 'Try the local join from the isolated test setup, then type quit in the server console and press Enter here' | Out-Null
        }
    } finally {
        $done = Complete-KitProcess $run $entry.Evidence 1200
        Save-GameEvidence $copy $entry.Evidence
    }
    if ($done.TimedOut -or $done.ExitCode -ne 0) { throw "The reference process did not exit cleanly. See $Label in results." }
}

function Invoke-ControlSessions([string]$GamePath, [string]$RetailPath) {
    $number = 0
    do {
        Write-Host '1 Unbound Original  2 Unbound Original raw  3 Unbound CoD2x  4 Retail Original  5 CoD2x reference'
        $selection = Read-Host 'Choose a game for the controls and sound checks, or press Enter to finish this section'
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
