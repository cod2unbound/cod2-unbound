function Find-RetailPaths {
    # Query InstallPath only, without reading either view's CD key.
    foreach ($view in 'Registry32', 'Registry64') {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', $view)
        try {
            $key = $baseKey.OpenSubKey('SOFTWARE\Activision\Call of Duty 2')
            if ($key) {
                try {
                    $path = $key.GetValue('InstallPath')
                    if ($path) { [pscustomobject]@{ View = $view; InstallPath = $path } }
                } finally { $key.Dispose() }
            }
        } finally { $baseKey.Dispose() }
    }
}

function Invoke-DataCheck([string]$Name, [string]$GamePath, [switch]$Invalid, [string]$TestHome) {
    $entry = New-RunContext $Name
    $homePath = if ($TestHome) { $TestHome } else { New-Folder (Join-Path $entry.Folder 'game') }
    $arguments = (Get-EngineArgs $homePath $GamePath) + ' +set dedicated 1 +set net_noudp 1 +writeconfig kit-path.cfg +quit'
    $run = Start-KitProcess (Get-Binary 'original' -Server) $arguments $entry.Folder
    $result = Complete-KitProcess $run $entry.Evidence 60
    Save-GameEvidence $homePath $entry.Evidence
    if ($result.TimedOut) { throw 'The game data check timed out.' }
    $found = Select-String -LiteralPath $result.Stdout -Pattern '^Game data \((.+)\): (.+)$' | Select-Object -First 1
    $written = Test-Path -LiteralPath (Join-Path $homePath 'main\kit-path.cfg')
    if ($Invalid) {
        $expected = if ($TestHome) { 'writable game folder under LocalAppData' } else { 'selected game folder must contain' }
        if ($result.ExitCode -eq 0 -or $found -or $written -or
            -not (Select-String -LiteralPath (Join-Path $entry.Folder 'stderr.txt') -SimpleMatch $expected -Quiet)) {
            throw 'The game did not reject the folder with the expected diagnostic.'
        }
        if ($TestHome) { Write-Host 'The game refused to save settings outside the test folder.' }
        else { Write-Host 'The game refused the folder with missing game files.' }
    } elseif ($result.ExitCode -eq 0 -and $found -and $written) {
        [pscustomobject]@{ Source = $found.Matches[0].Groups[1].Value; Path = $found.Matches[0].Groups[2].Value }
    } elseif ($GamePath -or $found) {
        throw 'The game did not finish the data check. See results.'
    } else {
        if (-not (Select-String -LiteralPath (Join-Path $entry.Folder 'stderr.txt') -SimpleMatch 'data was not found' -Quiet)) {
            throw 'The game exited before reporting whether it found the archives.'
        }
        Write-Host 'The game did not find an installation automatically.'
    }
}

function Test-GameData([string]$Selected) {
    @(Find-RetailPaths) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Results 'retail-paths.json') -Encoding UTF8
    $game = Get-DataRoot $Selected
    $explicit = Invoke-DataCheck 'discovery-chosen-folder' $game
    if ($explicit.Source -ne 'command line') { throw 'The explicit folder did not take precedence.' }
    Invoke-DataCheck 'discovery-missing-archives' (New-Folder (Join-Path $Private 'empty')) -Invalid
    $link = Join-Path $Private ('Game data + ' + [char]0x6771 + ' link')
    New-Item -ItemType Junction -Path $link -Target $game | Out-Null
    try {
        if (-not (Invoke-DataCheck 'discovery-junction' $link)) { throw 'The install junction was refused.' }
    }
    finally { [IO.Directory]::Delete($link) }
    $outside = New-Folder (Join-Path $env:TEMP ('cod2-kit-' + [guid]::NewGuid().ToString('N')))
    $badHome = Join-Path $Private 'profile-link'
    try {
        New-Item -ItemType Junction -Path $badHome -Target $outside | Out-Null
        Invoke-DataCheck 'discovery-unsafe-profile' $game -Invalid -TestHome $badHome
        if (Get-ChildItem -LiteralPath $outside -Force) { throw 'The unsafe profile target was written.' }
    } finally {
        if (Test-Path -LiteralPath $badHome) { [IO.Directory]::Delete($badHome) }
        [IO.Directory]::Delete($outside)
    }
}

function Get-RetailPath([string]$Provided, [string]$Selected) {
    if ($Provided) { return Get-DataRoot $Provided }
    $expected = '8f482e647b250babe098c6e3deb10129f9a935d013e852c99689cf8dff0d12cc'
    $candidates = @($Selected) + @(Find-RetailPaths | ForEach-Object InstallPath)
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        $exe = Join-Path $candidate 'CoD2MP_s.exe'
        if ((Test-Path -LiteralPath $exe) -and (Get-FileHash -LiteralPath $exe).Hash -ieq $expected) {
            return Get-DataRoot $candidate
        }
    }
    $answer = Read-Host 'Paste the folder for your Call of Duty 2 version 1.3 disc install, or press Enter to skip'
    if ($answer) { Get-DataRoot $answer }
}

function Test-RetailMouse([string]$RetailPath) {
    if (-not $RetailPath) { Write-Host 'Retail mouse comparison skipped.'; return }
    $entry = New-RunContext 'retail-mouse'
    $exe = Join-Path $RetailPath 'CoD2MP_s.exe'
    $renderer = Join-Path $RetailPath 'gfx_d3d_mp_x86_s.dll'
    $miles = Join-Path $RetailPath 'mss32.dll'
    @($exe, $renderer, $miles) | Get-FileHash | Select-Object @{n='File';e={Split-Path $_.Path -Leaf}},Hash |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $entry.Evidence 'retail-files.json')
    if ((Get-FileHash -LiteralPath $exe).Hash -ine '8f482e647b250babe098c6e3deb10129f9a935d013e852c99689cf8dff0d12cc') {
        throw 'This disc game is a different build from the one used for the mouse comparison.'
    }
    Copy-Item -LiteralPath $exe,$renderer -Destination $entry.Folder
    Copy-Item -LiteralPath $miles -Destination (Join-Path $entry.Folder 'miles_retail.dll')
    Copy-Item -LiteralPath (Join-Path $Kit 'probes\mouse\mss32.dll') -Destination $entry.Folder
    $retail = Start-KitProcess (Join-Path $entry.Folder 'CoD2MP_s.exe') '' $entry.Folder
    $capturedRetail = Complete-KitProcess $retail (New-Folder (Join-Path $entry.Evidence 'retail')) 45
    $retailRows = @($retail.Output.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_ })
    $port = Start-KitProcess (Join-Path $Kit 'probes\mouse\port-mouse.exe') '' $entry.Folder
    $capturedPort = Complete-KitProcess $port (New-Folder (Join-Path $entry.Evidence 'unbound')) 45
    $portRows = @($port.Output.GetAwaiter().GetResult() -split '\r?\n' | Where-Object { $_ })
    if ($capturedRetail.ExitCode -ne 0 -or $capturedPort.ExitCode -ne 0 -or
        $capturedRetail.TimedOut -or $capturedPort.TimedOut -or $retailRows.Count -ne 4096 -or $portRows.Count -ne 4096) {
        throw 'One of the two games did not produce all 4,096 mouse readings.'
    }
    for ($i = 0; $i -lt 4096; $i++) {
        if ($retailRows[$i] -cne $portRows[$i]) { throw "The first mouse reading that differs is number $($i + 1)." }
    }
    'All 4,096 mouse readings matched.' | Set-Content -LiteralPath (Join-Path $entry.Evidence 'comparison.txt')
}
