function Get-KitSelectedServerRow([IntPtr]$App) {
    $list = Find-KitControl $App 'ServerList'
    if (-not $list) { return }
    $selection = $list.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern)
    $row = $selection.Current.GetSelection() | Select-Object -First 1
    if ($row) {
        [pscustomobject]@{ Name=$row.Current.Name; RuntimeId=$row.GetRuntimeId() -join '.' }
    }
}

function Wait-KitFixtureRefreshComplete([IntPtr]$App) {
    Wait-KitCondition {
        $status = Find-KitControl $App 'RefreshStatus' -AllowOffscreen
        $button = Find-KitControl $App 'RefreshButton'
        $status -and $button -and $button.Current.Name -eq 'Refresh server list' -and
            $status.Current.Name -match '^246 servers\s'
    } 45 '246 replies and a completed fixture refresh' | Out-Null
}

function Invoke-KitRefreshNavigation {
    Stop-KitIfNoGame
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    $entry = New-RunContext 'local-refresh-fixture'
    $ready = Join-Path $entry.Folder 'master-port.txt'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $argsText = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Stage "{1}"' -f
        (Join-Path $Kit 'Local-Refresh-Fixture.ps1'), $entry.Folder
    $master = Start-KitProcess $ps $argsText $entry.Folder -NoWindow
    $run = $null
    try {
        $settings = Get-KitLauncherSettings
        $settings.COD2_UNBOUND_TEST_MASTER_PORT = Wait-KitCondition {
            if ($master.Process.HasExited) { throw 'The local refresh fixture exited before opening its socket.' }
            if (Test-Path -LiteralPath $ready) { [IO.File]::ReadAllText($ready) }
        } 15 'the loopback refresh fixture'

        $run = Start-KitProcess $exe '' $LauncherHome $settings
        $app = Get-KitLauncherWindow $run.Process
        Wait-KitHome $app
        Focus-KitWindow $app
        Invoke-KitControl $app 'ServersItem'
        Start-KitPublicRefresh $app
        Wait-KitFixtureRefreshComplete $app
        Invoke-KitControl $app 'SortName'
        Select-KitServerRow $app 'Fixture 002'
        $original = Wait-KitCondition {
            $row = Get-KitSelectedServerRow $app
            if ($row.Name -like 'Fixture 002,*') { return $row }
        } 12 'the selected fixture row'
        $receipt = Join-Path $Results 'refresh-row-identity.txt'
        "before: $($original.Name) [$($original.RuntimeId)]" | Set-Content -LiteralPath $receipt -Encoding UTF8

        Invoke-KitControl $app 'RefreshButton'
        Wait-KitCondition {
            $status = Find-KitControl $app 'RefreshStatus' -AllowOffscreen
            $status -and $status.Current.Name -match '^\d+ of 252 answered$'
        } 10 '252 addresses to be in the refresh' | Out-Null
        Invoke-KitControl $app 'HomeItem'
        Wait-KitCondition { Find-KitControl $app 'HeroTitle' } 5 'Home to open during the refresh' | Out-Null
        Invoke-KitControl $app 'ServersItem'
        Wait-KitCondition { Find-KitControl $app 'ServerList' } 5 'Servers to reopen' | Out-Null
        $button = Find-KitControl $app 'RefreshButton'
        if ($button.Current.Name -ne 'Stop refresh') {
            throw 'Home and Servers were reached only after the refresh stopped.'
        }
        'Home and Servers opened while the refresh was still running.' | Add-Content -LiteralPath $receipt
        foreach ($phase in 'during', 'complete') {
            if ($phase -eq 'complete') { Wait-KitFixtureRefreshComplete $app }
            $row = Get-KitSelectedServerRow $app
            "$phase $($row.Name) [$($row.RuntimeId)]" | Add-Content -LiteralPath $receipt
            if ($row.Name -notlike 'Fixture 002,*' -or $row.RuntimeId -cne $original.RuntimeId) {
                throw "The selected fixture row lost its identity $phase."
            }
        }
        '246 of 252 fixture addresses answered.' | Add-Content -LiteralPath $receipt
        Save-KitSurface $app 'refresh-complete' @('ServerList','RefreshStatus')
    } finally {
        try { if ($run) { Stop-KitLauncher $run $Results } }
        finally {
            [IO.File]::WriteAllText((Join-Path $entry.Folder 'master-stop.txt'), '')
            Complete-KitProcess $master $entry.Evidence 5 | Out-Null
            Save-Text (Join-Path $entry.Folder 'master-receipt.txt') (Join-Path $entry.Evidence 'master-receipt.txt')
        }
    }
}
