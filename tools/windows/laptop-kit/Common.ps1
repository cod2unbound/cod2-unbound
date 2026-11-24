#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Kit = $PSScriptRoot
$LocalData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if (-not $LocalData -and $Mode -eq 'Status') { $LocalData = $Kit }
$AppDataRoot = Join-Path $LocalData 'CoD2 Unbound'

function Assert-Offline {
    # An online PC is allowed only when the Mac driver asked for it. Every start
    # stays pinned to this PC by the loopback endpoints in Start-KitProcess.
    if ($env:COD2_KIT_ONLINE -eq '1') { return }
    $routes = @(Get-NetRoute | Where-Object { $_.DestinationPrefix -in @('0.0.0.0/0', '::/0') })
    if ($routes.Count) { throw 'Unplug internet access and disconnect Wi-Fi and VPN. Only an isolated LAN with no gateway is allowed.' }
}

function Assert-StandardUser([switch]$RecordOlderWindows) {
    $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $arch = $env:PROCESSOR_ARCHITECTURE
    $armRehearsal = $arch -eq 'ARM64' -and $env:COD2_KIT_ARM_REHEARSAL -eq '1'
    if (($arch -ne 'AMD64' -and -not $armRehearsal) -or -not [Environment]::Is64BitProcess -or
        (-not $RecordOlderWindows -and [int]$version.CurrentBuildNumber -lt 19041)) {
        throw 'Use 64-bit Windows PowerShell on Windows 10 (22H2) or Windows 11, on an x64 PC. The minimum Windows build is 19041.'
    }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell normally from File Explorer, without choosing Run as administrator.'
    }
    Assert-Offline
}

function Get-KitWindowsVersion {
    $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    # Windows 11 can still report "Windows 10" in the registry's ProductName.
    $family = if ([int]$version.CurrentBuildNumber -ge 22000) { 'Windows 11' } else { 'Windows 10' }
    $release = if ($version.DisplayVersion) { $version.DisplayVersion } else { $version.ReleaseId }
    [pscustomobject]@{ Family = $family; Release = $release; Edition = $version.EditionID
        Build = "$($version.CurrentBuildNumber).$($version.UBR)"; BuildNumber = [int]$version.CurrentBuildNumber }
}

function New-KitLock {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    [Threading.Mutex]::new($false, "Global\CoD2UnboundWindowsKit-$sid")
}

function Get-EditionName([string]$Edition) {
    if ($Edition -eq 'original') { 'Original 1.3' } else { 'CoD2x' }
}

function Get-GraphicsMode([string]$Renderer) {
    if ($Renderer -eq 'dx9') { 'Direct3D 9' } else { 'DirectX 7 mode' }
}

function New-Folder([string]$Path) {
    [IO.Directory]::CreateDirectory($Path).FullName
}

function Remove-KitScratch([string]$Path) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if (-not [IO.Directory]::Exists($Path)) { return }
        try {
            [IO.Directory]::Delete($Path, $true)
            return
        } catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -eq 3) { throw }
            # A recently exited Windows executable can still be held open briefly.
            Start-Sleep -Seconds 1
        }
    }
}

function Get-DataRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $selected = $Path.Trim().Trim('"')
    # The native engine reports a resolved Win32 device path. PowerShell 5.1
    # cannot use that prefix with Join-Path or hand it to the launcher.
    if ($selected.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) {
        $selected = '\\' + $selected.Substring(8)
    } elseif ($selected -match '^\\\\\?\\[a-zA-Z]:\\') {
        $selected = $selected.Substring(4)
    }
    $folder = Get-Item -LiteralPath $selected
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
    $data = Read-KitText $Source
    Protect-KitText $data | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Read-KitText([string]$Path) {
    # Windows programs also write UTF-16 and their system ANSI code page.
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $inputFile = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $buffer = [IO.MemoryStream]::new()
        try { $inputFile.CopyTo($buffer); $bytes = $buffer.ToArray() }
        finally { $buffer.Dispose() }
    } finally { $inputFile.Dispose() }
    if ($bytes.Length -ge 2 -and ($bytes[0] -in 254,255 -or $bytes[1] -eq 0)) {
        $encoding = if ($bytes[0] -eq 254) { [Text.Encoding]::BigEndianUnicode } else { [Text.Encoding]::Unicode }
        return $encoding.GetString($bytes).TrimStart([char]0xfeff)
    }
    try { [Text.UTF8Encoding]::new($true, $true).GetString($bytes).TrimStart([char]0xfeff) }
    catch [Text.DecoderFallbackException] { [Text.Encoding]::Default.GetString($bytes) }
}

function Protect-KitText([string]$Text) {
    $line = $Text -replace '(?im)(cd[ -]?key|codkey|keycode|hwid2?|regid|serial[ _-]*number|password|computername|username)\s*["'']?\s*[:=]\s*[^\r\n]*', '$1=[redacted]'
    $line = $line -replace '(?i)\b[0-9a-z]{18,20}\b|\b[0-9a-f]{32}\b|\b(?:[0-9a-z]{4}-){4}[0-9a-z]{4}\b', '[redacted]'
    foreach ($value in @($env:USERPROFILE, $env:COMPUTERNAME, $env:USERNAME) + @($script:KitSerials)) {
        if ($value) {
            foreach ($variant in @($value, $value.Replace('\','/'), $value.Replace('\','\\')) | Select-Object -Unique) {
                $pattern = '(?<![\p{L}\p{N}])' + [regex]::Escape($variant) + '(?![\p{L}\p{N}])'
                $line = $line -replace $pattern, '[redacted]'
            }
        }
    }
    $line
}

function Find-KitExternalLookup([string]$Text) {
    $local = '(?:localhost|127(?:\.\d{1,3}){3}|\[::1\]|::1)'
    foreach ($line in $Text -split '\r?\n') {
        if ($line -match '(?i)\bresolving\s+(?:(?:master server|authorization server|autoupdate server)\s+)?(\S+)' -and
            $Matches[1].TrimEnd('.') -notmatch "^$local(?::\d+)?$") {
            return $line
        }
        if ($line -match '(?i)\b(\S+)\s+resolved to\s+(\S+)') {
            $source = $Matches[1]
            $address = $Matches[2]
            if (($source -ine 'AutoUpdate' -and $source -notmatch "^$local(?::\d+)?$") -or
                $address -notmatch "^$local(?::\d+)?$") { return $line }
        }
    }
}

function Save-KitEngineAudit([string]$Text, [string]$Evidence) {
    $adapter = @($Text -split '\r?\n' | Where-Object {
        $_ -match '(?i)^(?:Using D3D9 adapter|Direct3D 9 adapter|Direct3D device:|GL_RENDERER:)'
    } | Select-Object -Unique)
    if ($adapter.Count) {
        $adapter | Add-Content -LiteralPath (Join-Path $Evidence 'engine-adapter.txt') -Encoding UTF8
        if (($adapter -join ' ') -match 'Intel') {
            'The game reported Intel. See START-HERE.md for the per-app graphics choice and a second labelled run.' |
                Add-Content -LiteralPath (Join-Path $Evidence 'engine-adapter.txt') -Encoding UTF8
        }
    }
    if ($env:COD2_KIT_ONLINE -eq '1') {
        $lookup = Find-KitExternalLookup $Text
        if ($lookup) {
            $script:KitExternalLookup = $lookup
            Protect-KitText $lookup | Add-Content -LiteralPath (Join-Path $Evidence 'external-lookup.txt') -Encoding UTF8
        }
    }
}

function Start-KitProcess {
    param([string]$Exe, [string]$Arguments, [string]$Folder, [hashtable]$Settings = @{}, [switch]$NoWindow, [string]$WorkingDirectory)
    Assert-Offline
    $values = @{
        HOME = $Folder
        CFFIXED_USER_HOME = $Folder
        COD2_PROFILE_CHOSEN = '1'
        COD2X_TEST_ISOLATE_REGISTRY = '1'
        COD2X_TEST_SERVICE_URL = 'http://127.0.0.1:9'
        COD2X_TEST_UPDATER_ADDRESS = '127.0.0.1:9'
        COD2X_TEST_UPDATER_URL = 'http://127.0.0.1:9/api/updater/check'
        COD2_FRAME_TRACE_FILE = $null
        SYSDIFF_STATEHASH = $null
        SYSDIFF_MAXFRAMES = $null
        SYSDIFF_PLAYERS = $null
        SYSDIFF_CLIENTS = $null
        SYSDIFF_USERCMDS = $null
        SYSDIFF_REPLAY = $null
        SYSDIFF_SEED = $null
        SYSDIFF_DUMP = $null
    }
    foreach ($name in $Settings.Keys) {
        if ($name -like 'COD2X_TEST_*' -and $values[$name] -cne $Settings[$name]) {
            throw "Unexpected engine endpoint override: $name"
        }
        if ($name -in 'COD2_UNBOUND_TEST_UPDATE_ORIGIN','COD2_USAGE_URL','COD2_CRASH_URL' -and
            $Settings[$name] -notmatch '^http://127\.0\.0\.1:[0-9]+(?:/[^\s]*)?$') {
            throw "The launcher endpoint $name must stay on this PC."
        }
        $values[$name] = $Settings[$name]
    }

    # A per-child environment and one raw argument string preserve Unicode and engine quotes.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = $Arguments
    $info.WorkingDirectory = if ($WorkingDirectory) { $WorkingDirectory } else { $Folder }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $NoWindow.IsPresent
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($name in $values.Keys) {
        if ($null -eq $values[$name]) { $info.EnvironmentVariables.Remove($name) }
        else { $info.EnvironmentVariables[$name] = $values[$name] }
    }
    $process = [Diagnostics.Process]::Start($info)
    [pscustomobject]@{ Process = $process; Folder = $Folder; Engine = (Split-Path $Exe -Leaf) -match '^cod2_win32(?:_ded)?\.exe$'
        Output = $process.StandardOutput.ReadToEndAsync(); Errors = $process.StandardError.ReadToEndAsync() }
}

function Complete-KitProcess($Run, [string]$Evidence, [int]$Seconds = 120) {
    $timedOut = -not $Run.Process.WaitForExit(1000 * $Seconds)
    if ($timedOut) {
        if (-not $Run.Process.HasExited) { $Run.Process.Kill() }
        if (-not $Run.Process.WaitForExit(5000)) { throw 'The kit process did not exit.' }
    }
    $stdout = Join-Path $Run.Folder 'stdout.txt'
    $stderr = Join-Path $Run.Folder 'stderr.txt'
    foreach ($stream in @(@{ Task=$Run.Output; Path=$stdout }, @{ Task=$Run.Errors; Path=$stderr })) {
        $content = if ($stream.Task.Wait(3000)) { $stream.Task.GetAwaiter().GetResult() }
            else { 'An inherited output stream remained open after the process exited.' }
        [IO.File]::WriteAllText($stream.Path, $content)
        if ($Run.Engine) { Save-KitEngineAudit $content $Evidence }
    }
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
    if ($checked.TimedOut -or $checked.ExitCode -ne 0) { throw "Windows could not complete the test key check ($Action)." }
}

function Test-SavedKitKey {
    $listed = & "$env:SystemRoot\System32\cmdkey.exe" /list
    if ($LASTEXITCODE -ne 0) { throw 'Windows could not check for an existing saved CoD2 Unbound key.' }
    [bool]($listed -match 'CoD2 Unbound/CD Key')
}

function Assert-NoSavedKitKey {
    if (Test-SavedKitKey) {
        throw 'This account already has a saved CoD2 Unbound key. Leave it in place and use another account for the kit.'
    }
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
        $log = Join-Path $main $name
        if (Test-Path -LiteralPath $log -PathType Leaf) {
            $content = Read-KitText $log
            Save-KitEngineAudit $content $Evidence
            Protect-KitText $content | Set-Content -LiteralPath (Join-Path $Evidence $name) -Encoding UTF8
        }
    }
    $shots = Join-Path $main 'screenshots'
    if (Test-Path -LiteralPath $shots) {
        Get-ChildItem -LiteralPath $shots -File -Filter 'kit-*.jpg' |
            Copy-Item -Destination $Evidence
    }
}

function Get-KitSerials {
    foreach ($className in 'Win32_BIOS','Win32_BaseBoard','Win32_DiskDrive','Win32_SystemEnclosure') {
        try {
            Get-CimInstance -ClassName $className -OperationTimeoutSec 4 -ErrorAction Stop |
                ForEach-Object { "$($_.SerialNumber)".Trim() } | Where-Object { $_.Length -ge 3 }
        } catch { }
    }
}
