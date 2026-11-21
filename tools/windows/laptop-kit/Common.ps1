#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Kit = $PSScriptRoot
$LocalData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$AppDataRoot = Join-Path $LocalData 'CoD2 Unbound'

function Assert-Offline {
    $routes = @(Get-NetRoute | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') })
    if ($routes.Count) { throw 'Unplug internet access and disconnect Wi-Fi and VPN. Only an isolated LAN with no gateway is allowed.' }
}

function Assert-StandardUser {
    $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or -not [Environment]::Is64BitProcess -or
        [int]$version.CurrentBuildNumber -lt 22000) {
        throw 'Use 64-bit Windows PowerShell on Windows 11 x64.'
    }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open an ordinary PowerShell window in the disposable standard account.'
    }
    Assert-Offline
}

function New-Folder([string]$Path) {
    [IO.Directory]::CreateDirectory($Path).FullName
}

function Get-DataRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $folder = Get-Item -LiteralPath ($Path.Trim().Trim('"'))
    if ($folder.Name -ieq 'main') { $folder = $folder.Parent }
    foreach ($index in 0..15) {
        $archive = Join-Path $folder.FullName ('main\iw_{0:00}.iwd' -f $index)
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw "Missing archive iw_$('{0:00}' -f $index).iwd." }
    }
    return $folder.FullName
}

function Read-GamePath([string]$Detected) {
    if ($Detected) {
        Write-Host "The game found $Detected"
        $value = Read-Host 'Press Enter to use this folder, or paste another installed game folder'
        if (-not $value) { return $Detected }
    } else {
        $value = Read-Host 'Paste the Call of Duty 2 folder that contains main'
    }
    return Get-DataRoot $value
}

function Confirm-Step([string]$Label) {
    (Read-Host "$Label [Enter to continue, S to skip]") -ine 's'
}

function Engine-Path([string]$Path) {
    '"' + $Path.Replace('\','/') + '"'
}

function Save-Text([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { return }
    $data = @(Get-Content -LiteralPath $Source) | Where-Object {
        $_ -notmatch '(?i)cd.?key|codkey|keycode|hwid|regid|serial\s*number|device\s*(key|identifier)|machine\s*(name|id)|computer\s*name|user\s*name|\bGUID\b'
    } | ForEach-Object {
        $line = $_ -replace '(?i)\b[0-9A-Z]{20}\b', '[redacted]'
        if ($env:USERPROFILE) { $line = $line -replace [regex]::Escape($env:USERPROFILE), '%USERPROFILE%' }
        $line
    }
    Set-Content -LiteralPath $Destination -Value @($data) -Encoding UTF8
}

function Start-KitProcess {
    param([string]$Exe, [string]$Arguments, [string]$Folder, [hashtable]$Settings = @{})
    Assert-Offline
    $values = @{
        HOME = $Folder
        CFFIXED_USER_HOME = $Folder
        COD2_PROFILE_CHOSEN = '1'
        COD2X_TEST_ISOLATE_REGISTRY = '1'
        COD2X_TEST_SERVICE_URL = 'http://127.0.0.1:9'
        COD2X_TEST_UPDATER_ADDRESS = '127.0.0.1:9'
        COD2X_TEST_UPDATER_URL = 'http://127.0.0.1:9/api/updater/check'
        SYSDIFF_STATEHASH = $null
        SYSDIFF_MAXFRAMES = $null
        SYSDIFF_PLAYERS = $null
        SYSDIFF_CLIENTS = $null
        SYSDIFF_USERCMDS = $null
        SYSDIFF_REPLAY = $null
        SYSDIFF_SEED = $null
        SYSDIFF_DUMP = $null
    }
    foreach ($name in $Settings.Keys) { $values[$name] = $Settings[$name] }

    # A per-child environment and one raw argument string preserve Unicode and engine quotes.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = $Arguments
    $info.WorkingDirectory = $Folder
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($name in $values.Keys) {
        if ($null -eq $values[$name]) { $info.EnvironmentVariables.Remove($name) }
        else { $info.EnvironmentVariables[$name] = $values[$name] }
    }
    $process = [Diagnostics.Process]::Start($info)
    [pscustomobject]@{ Process = $process; Folder = $Folder
        Output = $process.StandardOutput.ReadToEndAsync(); Errors = $process.StandardError.ReadToEndAsync() }
}

function Complete-KitProcess($Run, [string]$Evidence, [int]$Seconds = 120) {
    $timedOut = -not $Run.Process.WaitForExit(1000 * $Seconds)
    if ($timedOut) {
        if (-not $Run.Process.HasExited) { $Run.Process.Kill() }
        $Run.Process.WaitForExit()
    }
    $stdout = Join-Path $Run.Folder 'stdout.txt'
    $stderr = Join-Path $Run.Folder 'stderr.txt'
    [IO.File]::WriteAllText($stdout, $Run.Output.GetAwaiter().GetResult())
    [IO.File]::WriteAllText($stderr, $Run.Errors.GetAwaiter().GetResult())
    foreach ($file in @($stdout, $stderr)) {
        Save-Text $file (Join-Path $Evidence ([IO.Path]::GetFileName($file)))
    }
    $result = [pscustomobject]@{ ExitCode = $Run.Process.ExitCode; TimedOut = $timedOut; Stdout = $stdout }
    $result | Select-Object ExitCode, TimedOut | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $Evidence 'exit.json') -Encoding UTF8
    $Run.Process.Dispose()
    return $result
}

function New-RunContext([string]$Name) {
    $folder = New-Folder (Join-Path $Private $Name)
    $evidence = New-Folder (Join-Path $Results $Name)
    [pscustomobject]@{ Folder = $folder; Evidence = $evidence }
}

function Invoke-TestKey([string]$Action) {
    $exe = Join-Path $Kit 'probes\test-key.exe'
    $log = New-RunContext ('key-' + $Action)
    $run = Start-KitProcess $exe $Action $log.Folder
    $checked = Complete-KitProcess $run $log.Evidence 15
    if ($checked.TimedOut -or $checked.ExitCode -ne 0) { throw "Synthetic key check '$Action' failed." }
}

function Start-TestSession {
    $listed = & "$env:SystemRoot\System32\cmdkey.exe" /list
    if ($LASTEXITCODE -ne 0 -or ($listed -match 'CoD2 Unbound/CD Key')) {
        throw 'This account may already have a CoD2 Unbound credential. Use a fresh disposable account.'
    }
    Invoke-TestKey 'seed'
}

function Get-Binary([string]$Edition, [switch]$Server, [string]$Root = (Join-Path $Kit 'portable')) {
    $file = if ($Server) { 'cod2_win32_ded.exe' } else { 'cod2_win32.exe' }
    Join-Path (Join-Path $Root $Edition) $file
}

function Get-EngineArgs([string]$HomePath, [string]$GamePath, [string]$Address = '127.0.0.1', [int]$Port = 29676) {
    $arguments = "+set fs_homepath $(Engine-Path $HomePath) +set logfile 2 +set developer 0" +
        " +set net_ip $Address +set net_port $Port +set net_noipx 1 +set net_lanauthorize 0" +
        ' +set sv_allowDownload 0 +set sv_punkbuster 0 +set sv_master1 "" +set sv_master2 "" +set sv_master3 "" +set sv_master4 "" +set sv_master5 ""'
    if ($GamePath) { $arguments += " +set fs_basepath $(Engine-Path $GamePath)" }
    $arguments
}

function Save-GameEvidence([string]$HomePath, [string]$Evidence) {
    $main = Join-Path $HomePath 'main'
    if (-not (Test-Path -LiteralPath $main)) { return }
    foreach ($name in 'console_mp.log', 'console_mp_server.log', 'games_mp.log', 'qconsole.log') {
        Save-Text (Join-Path $main $name) (Join-Path $Evidence $name)
    }
    $shots = Join-Path $main 'screenshots'
    if (Test-Path -LiteralPath $shots) {
        Get-ChildItem -LiteralPath $shots -File -Filter 'kit-*.jpg' |
            Copy-Item -Destination $Evidence
    }
}
