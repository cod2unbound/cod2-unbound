#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GamePath,
    [ValidatePattern('^[a-zA-Z0-9_-]{1,48}$')][string]$RunId = 'laptop',
    [ValidateSet('Run','Status','Step')][string]$Mode = 'Run',
    [ValidatePattern('^[a-z0-9-]*$')][string]$ExpectedStep,
    [string]$Answer,
    [ValidateSet('Default','Intel','Radeon')][string]$GraphicsLabel = 'Default',
    [string]$DriverUtc,
    [ValidatePattern('^[a-zA-Z0-9_-]{0,48}$')][string]$ProfileName,
    [switch]$InternetPlay
)

. (Join-Path $PSScriptRoot 'Common.ps1')
foreach ($script in 'Check-Machine','Check-GameData','Check-Clients','Check-Servers','Check-Replays',
    'Check-Launcher','Check-Installer','Check-Desktop','Check-Diagnostics','Check-Installed','Check-Frames',
    'Check-Updates','Check-Loopback','Check-Surfaces','Check-Identity','Check-PublicPlay','Check-Refresh','Check-Isolation') {
    . (Join-Path $PSScriptRoot "$script.ps1")
}

if ($ProfileName -and $InternetPlay) { throw 'A named private profile is only for the Fixture run.' }
$KitProfile = Join-Path $KitScratchRoot $(if ($ProfileName) { $ProfileName } else { $RunId })
$RunResults = Join-Path $Kit "results\$RunId"
$RunStart = Join-Path $RunResults 'started.txt'
$SlotFile = Join-Path $RunResults 'key-slot.txt'
$KitSafety = Join-Path $RunResults 'preflight'
$RunProfileName = Join-Path $RunResults 'profile-name.txt'
$GamePathFile = Join-Path $KitProfile 'game-path.txt'
$UpdateTrustFile = Join-Path $RunResults 'preflight\update-trust.txt'
$script:KitSerials = @()
. (Join-Path $PSScriptRoot 'Laptop-Steps.ps1')
if ($InternetPlay) {
    $InstallRoot = Join-Path $LocalData 'Programs\CoD2 Unbound'
    $Steps = @(
        Add-KitStep 'preflight' 'Check this laptop and the kit for internet play' {
            Assert-KitPublicPlay ($env:COD2_KIT_ONLINE -eq '1') ($env:COD2_KIT_ARM_REHEARSAL -eq '1')
            Save-KitPreflightComputer | Set-Content -LiteralPath (Join-Path $Results 'monitors.txt') -Encoding UTF8
            Test-KitFiles
            $path = if ($GamePath) { Get-DataRoot $GamePath } else { Find-KitDefaultRetail }
            if ($path) { [IO.File]::WriteAllText($GamePathFile, $path) }
        }
        Add-KitStep 'public-play' 'Use the installed play build on real game servers' {
            Invoke-KitPublicPlay
        } 'The play build will use your verified key to join real game servers and briefly list test rooms. Type YES to begin, or press Enter to leave this part out' -Consent
        Add-KitStep 'cleanup' 'Remove temporary files from the play check' { Remove-KitScratch $KitProfile }
    )
}

function Get-KitUpdateSkip([string]$Id) {
    if ($Id -notmatch '^(update-[1-5]|interrupt|restore)$') { return }
    if (-not [IO.File]::Exists($UpdateTrustFile)) { return }
    $status = [IO.File]::ReadAllText($UpdateTrustFile)
    if ($status -ne 'Valid') {
        "Windows does not trust this lab-signed Setup ($status). The seven update and restore checks were skipped."
    }
}

function Get-KitStatus {
    foreach ($step in $Steps) {
        $receipt = Join-Path $RunResults "$($step.Id)\result.json"
        if (-not (Test-Path -LiteralPath $receipt)) {
            $skip = Get-KitUpdateSkip $step.Id
            return [ordered]@{ Done = $false; Id = $step.Id; Title = $step.Title
                Prompt = if (-not $skip) { $step.Prompt }; Consent = [bool]($step.Consent -and -not $skip) }
        }
    }
    return [ordered]@{ Done = $true }
}

function Save-KitReceipt($Step, [string]$Status, [string]$Detail, [int]$Seconds) {
    $Detail = (Protect-KitText $Detail) -replace '[\r\n]+',' '
    $line = "$Status - $($Step.Title)"
    if ($Detail) { $line += ". $Detail" }
    if ($script:KitFrameLines.Count) { $line += '. ' + ($script:KitFrameLines -join ' | ') }
    $temporary = Join-Path $Results 'result.tmp'
    [ordered]@{ Id=$Step.Id; Status=$Status; Line=$line; Seconds=$Seconds } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $temporary -Encoding UTF8
    [IO.File]::Move($temporary, (Join-Path $Results 'result.json'))
    Write-Host $line
}

function Invoke-LaptopStep($Step, [string]$Reply) {
    $Private = New-Folder (Join-Path $KitProfile $Step.Id)
    $Results = New-Folder (Join-Path $RunResults $Step.Id)
    $script:KitAttentionTimedOut = $false
    $script:KitExternalLookup = $null
    $script:KitFrameLines = @()
    $env:COD2_FRAME_TRACE_FILE = $null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($skip = Get-KitUpdateSkip $Step.Id) {
        Save-KitReceipt $Step 'SKIP' $skip 0
        return
    }
    if ($Step.Id -eq 'interrupt' -and (Test-Path -LiteralPath (Join-Path $KitProfile 'restart-witness.txt'))) {
        $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')
        $prior = Get-Content -LiteralPath (Join-Path $KitProfile 'restart-witness.txt') -TotalCount 1
        if ($boot -ne $prior) {
            Save-KitReceipt $Step 'PASS' 'Windows restarted after Setup saved its backup' 0
            return
        }
    }
    if ($Step.Consent -and $Reply -cne 'YES') {
        Save-KitReceipt $Step 'SKIP' 'No permission given' 0
        return
    }
    if ($Step.Prompt -and -not $Step.Consent) {
        if ([string]::IsNullOrWhiteSpace($Reply) -or $Reply -ieq 'skip') {
            Save-KitReceipt $Step 'SKIP' 'No observation recorded' 0
        } else {
            Save-KitReceipt $Step 'NOTE' $Reply 0
        }
        return
    }
    try {
        if (Test-Path -LiteralPath $GamePathFile) {
            $GamePath = [IO.File]::ReadAllText($GamePathFile)
        }
        $receipt = Join-Path $Results 'running.txt'
        if (Test-Path -LiteralPath $receipt) {
            if ($Step.Consent) {
                throw 'This step was interrupted. See its evidence and run the cleanup step before starting another run.'
            }
            Remove-Item -LiteralPath $receipt
        }
        [IO.File]::WriteAllText($receipt, [DateTime]::UtcNow.ToString('o'))
        & $Step.Action $Step | Out-Null
        if ($script:KitExternalLookup) { throw "External engine lookup: $script:KitExternalLookup" }
        Save-KitReceipt $Step 'PASS' '' ([int]$watch.Elapsed.TotalSeconds)
    } catch {
        $status = if ($_.Exception -is [NotSupportedException]) { 'SKIP' } else { 'FAIL' }
        $detail = $_.Exception.Message
        if ($script:KitExternalLookup -and -not $detail.Contains($script:KitExternalLookup)) {
            $status = 'FAIL'
            $detail += " External engine lookup: $script:KitExternalLookup"
        }
        Save-KitReceipt $Step $status $detail ([int]$watch.Elapsed.TotalSeconds)
    }
}

if ($Mode -eq 'Status') {
    Get-KitStatus | ConvertTo-Json -Compress
    return
}

Assert-StandardUser -RecordOlderWindows
$script:KitSerials = Get-KitSerials
foreach ($location in @($AppDataRoot, (Split-Path $KitProfile -Parent), $KitProfile,
        (Join-Path $Kit 'results'), $RunResults)) {
    if ((Test-Path -LiteralPath $location) -and ((Get-Item -LiteralPath $location -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Move the kit or test folder to an ordinary Windows folder.'
    }
}
$lock = New-KitLock
$owned = $false
try {
    try { $owned = $lock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'A kit check is already running in this account.' }
    New-Folder $RunResults | Out-Null
    if (-not (Test-Path -LiteralPath $RunStart)) {
        if ($ProfileName -and (Test-Path -LiteralPath $KitProfile)) {
            throw 'The named private test profile already exists.'
        }
        if ($ProfileName) { [IO.File]::WriteAllText($RunProfileName, $ProfileName) }
        if (-not $InternetPlay) {
            [IO.File]::WriteAllText($SlotFile, [guid]::NewGuid().ToString('N'))
        }
        [IO.File]::WriteAllText($RunStart, [DateTime]::UtcNow.ToString('o'))
    } else {
        $previous = if ([IO.File]::Exists($RunProfileName)) { [IO.File]::ReadAllText($RunProfileName) } else { '' }
        if ($previous -ine [string]$ProfileName) { throw 'Resume with the same private profile name.' }
    }
    if (-not $InternetPlay) {
        $KitKeySlot = [IO.File]::ReadAllText($SlotFile)
        [void](Get-KitKeySlot)
    }
    do {
        $next = Get-KitStatus
        if ($next.Done) {
            Write-Host "Finished. Results are in $RunResults"
            return
        }
        if ($Mode -eq 'Step' -and $ExpectedStep -cne $next.Id) {
            throw "The next step is $($next.Id). Refresh the status before starting it."
        }
        if ($next.Id -ne 'preflight' -and $next.Id -notin 'collect','cleanup') {
            $preflight = Get-Content -LiteralPath (Join-Path $RunResults 'preflight\result.json') -Raw | ConvertFrom-Json
            if ($preflight.Status -ne 'PASS') {
                $Results = New-Folder (Join-Path $RunResults $next.Id)
                Save-KitReceipt $next 'SKIP' 'The initial file and safety check failed' 0
                if ($Mode -eq 'Step') { return }
                continue
            }
        }
        if ($Mode -eq 'Run' -and $next.Prompt) {
            $Answer = Read-Host $next.Prompt
        }
        Invoke-LaptopStep ($Steps | Where-Object Id -eq $next.Id) $Answer
    } while ($Mode -eq 'Run')
} finally {
    if ($owned) { $lock.ReleaseMutex() }
    $lock.Dispose()
}
