#Requires -Version 5.1
param([string]$Stage, [string]$Operation, [string]$Step, [string]$Answer, [string]$GamePath,
    [string]$Sid, [switch]$ArmRehearsal)
$ErrorActionPreference = 'Stop'
$kit = Join-Path $Stage 'CoD2-Unbound-Windows-Kit'
$runner = Join-Path $kit 'Run-Laptop.ps1'
$taskName = "CoD2UnboundKit-$([IO.Path]::GetFileName($Stage))"
$runName = [IO.Path]::GetFileName($Stage)
$resultRoot = Join-Path $kit "results\$runName"
$reply = Join-Path $Stage 'request.json'

function Get-DesktopTask {
    try { $taskFolder.GetTask($taskName) }
    catch {
        if ($_.Exception.GetBaseException().HResult -ne -2147024894) { throw }
    }
}

if ($Operation -in 'status','queue','finish') {
    # The SSH network logon need not have read access to the CIM namespace.
    # Task Scheduler's local COM interface works with per-user task permissions.
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $taskFolder = $scheduler.GetFolder('\')
}

if ($Operation -eq 'status') {
    $status = & $runner -RunId $runName -Mode Status | ConvertFrom-Json
    $task = Get-DesktopTask
    $state = if (-not $task) { 'Absent' } elseif ($task.State -in 2,4) { 'Running' } else { 'Ready' }
    $receipts = @(Get-ChildItem -LiteralPath $resultRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName 'result.json')
    } | Select-Object -ExpandProperty Name)
    @{ Next=$status; Task=$state; Receipts=$receipts } | ConvertTo-Json -Compress -Depth 4
    return
}
if ($Operation -eq 'queue') {
    $status = & $runner -RunId $runName -Mode Status | ConvertFrom-Json
    if ($status.Done -or $status.Id -cne $Step) { throw 'The Windows step has already changed.' }
    $task = Get-DesktopTask
    if ($task -and $task.State -in 2,4) { throw 'The Windows step is already running.' }
    if (-not $Sid) { $Sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
    $definition = $scheduler.NewTask(0)
    $interactiveToken = 3
    $definition.Principal.UserId = $Sid
    $definition.Principal.LogonType = $interactiveToken
    $definition.Principal.RunLevel = 0
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $worker = Join-Path $kit 'Remote-Kit.ps1'
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Stage "{1}" -Operation worker' -f $worker,$Stage
    $action = $definition.Actions.Create(0)
    $action.Path = $ps
    $action.Arguments = $arguments
    $definition.Settings.ExecutionTimeLimit = 'PT18M'
    $definition.Settings.MultipleInstances = 2  # TASK_INSTANCES_IGNORE_NEW
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false
    @{ Step=$Step; Answer=$Answer; GamePath=$GamePath; ArmRehearsal=$ArmRehearsal.IsPresent } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $reply -Encoding UTF8
    $workerLog = Join-Path $Stage 'worker-error.txt'
    if (Test-Path -LiteralPath $workerLog) { Remove-Item -LiteralPath $workerLog }
    $createOrUpdate = 6
    $task = $taskFolder.RegisterTaskDefinition($taskName, $definition, $createOrUpdate, $Sid, $null, $interactiveToken, $null)
    $null = $task.Run($null)
    return
}
if ($Operation -eq 'worker') {
    $request = Get-Content -LiteralPath $reply -Raw | ConvertFrom-Json
    if ($request.ArmRehearsal) { $env:COD2_KIT_ARM_REHEARSAL = '1' }
    try {
        & $runner -RunId $runName -Mode Step -ExpectedStep $request.Step -Answer $request.Answer -GamePath $request.GamePath
    } catch {
        $workerLog = Join-Path $Stage 'worker-error.txt'
        [IO.File]::WriteAllText($workerLog, $_.Exception.Message)
        exit 1
    }
    return
}
if ($Operation -eq 'archive') {
    if ($Step -notmatch '^[a-z0-9-]+$') { throw 'Invalid result directory.' }
    $folder = Join-Path $resultRoot $Step
    $receipt = Join-Path $folder 'result.json'
    if (-not (Test-Path -LiteralPath $receipt)) { throw 'The requested step has not finished.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $Stage "$Step.zip"
    $pending = "$zip.partial"
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    [AppContext]::SetSwitch('Switch.System.IO.Compression.ZipFile.UseBackslash', $false)
    [IO.Compression.ZipFile]::CreateFromDirectory($folder, $pending, [IO.Compression.CompressionLevel]::Fastest, $true)
    if (Test-Path -LiteralPath $zip) { [IO.File]::Replace($pending, $zip, $null) }
    else { [IO.File]::Move($pending, $zip) }
    Get-Content -LiteralPath $receipt -Raw
    return
}
if ($Operation -eq 'finish') {
    $status = & $runner -RunId $runName -Mode Status | ConvertFrom-Json
    if (-not $status.Done) { throw 'There are unfinished Windows steps.' }
    $task = Get-DesktopTask
    if ($task -and $task.State -in 2,4) { throw 'Wait for the last Windows step to exit.' }
    if ($task) { $taskFolder.DeleteTask($taskName, 0) }
    $cleanup = Get-Content -LiteralPath (Join-Path $resultRoot 'cleanup\result.json') -Raw | ConvertFrom-Json
    if ($cleanup.Status -eq 'PASS') {
        Remove-Item -LiteralPath $Stage -Recurse
        'Windows staging removed.'
    } else {
        'Windows kept the staging folder because cleanup was skipped or failed.'
    }
    return
}
throw "Unknown kit operation: $Operation"
