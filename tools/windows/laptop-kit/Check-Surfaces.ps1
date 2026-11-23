function Save-KitSurface([IntPtr]$Window, [string]$Name, [string[]]$Controls) {
    foreach ($control in $Controls) {
        Wait-KitCondition { Find-KitControl $Window $control -Name -AllowOffscreen } 12 $control | Out-Null
    }
    Focus-KitWindow $Window
    Save-KitWindow $Window (Join-Path $Results "$Name.png")
    $Controls | Add-Content -LiteralPath (Join-Path $Results 'automation-names.txt') -Encoding UTF8
}

function Invoke-KitSurface([string]$Page) {
    Stop-KitIfNoGame
    Initialize-KitDesktop
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $exe = Get-KitStartTarget
    $LauncherHome = New-KitLauncherTestHome $LauncherHome $Private
    Set-KitLauncherProfile 'original' 'dx9'
    if ($Page -eq 'home') {
        [IO.File]::WriteAllText((Join-Path $LauncherHome 'launcher\launcher-library.json'),
            '{"recent":["127.0.0.1:9"]}')
    }
    if ($Page -eq 'library') {
        $demo = Join-Path $Kit 'frame-references\original\smoke-mp_toujane.dm_1'
        $picture = Join-Path $Kit 'frame-references\original\dx9\dx9-mp_toujane-00-windows.png'
        Copy-Item -LiteralPath $demo -Destination (New-Folder (Join-Path $LauncherHome 'main\demos'))
        Copy-Item -LiteralPath $picture -Destination (New-Folder (Join-Path $LauncherHome 'main\screenshots'))
        $stats = New-Folder (Join-Path $LauncherHome 'main\stats')
        $summary = @{
            schemaVersion=1; endedAt=[DateTime]::UtcNow.ToString('o'); endReason='intermission'
            map='mp_toujane'; mode='dm'; durationSeconds=60; serverAddress='127.0.0.1:29792'
            ruleHints=@{}; frameCount=0; capSampleFrames=0; capFPS=0; pingSamples=0
            packetsReceived=0; packetsLost=0; snapshotsReceived=0
        }
        $summary | ConvertTo-Json -Compress |
            Set-Content -LiteralPath (Join-Path $stats 'matches.jsonl') -Encoding ASCII
    }
    $run = Start-KitProcess $exe '' $LauncherHome (Get-KitLauncherSettings)
    $hostedProcess = $null
    try {
        $app = Get-KitLauncherWindow $run.Process
        Wait-KitHome $app
        [void][KitDesktop]::ShowWindow($app, 3)
        Focus-KitWindow $app
        switch ($Page) {
            'home' {
                Invoke-KitControl $app 'More ways to launch' -Name
                Save-KitSurface $app 'game-menu' @('Open the game menu',('Connect to an address' + [char]0x2026))
                [KitDesktop]::Tap(27)
                $news = if (Find-KitControl $app 'See all' -Name) { 'See all' } else { "What$([char]0x2019)s New" }
                Invoke-KitControl $app $news -Name
                Save-KitSurface $app 'news' @('Done')
                Invoke-KitControl $app 'Done' -Name
            }
            'jump' {
                [KitDesktop]::Chord(17, 75)
                Wait-KitCondition { Find-KitControl $app 'Jump to' -Name } 12 'the Jump to box' | Out-Null
                [Windows.Forms.SendKeys]::SendWait('keyboard shortcuts')
                Save-KitSurface $app 'jump-search' @('Keyboard shortcuts')
                [KitDesktop]::Tap(13)
                Save-KitSurface $app 'shortcuts' @('Done')
                Invoke-KitControl $app 'Done' -Name
            }
            'hosting' {
                Invoke-KitControl $app 'ServersItem'
                Invoke-KitControl $app 'Hosting' -Name
                Invoke-KitControl $app 'New server' -Name
                Set-KitEdit $app 'Server name' 'Kit Export' -Name
                Invoke-KitControl $app 'Start server' -Name
                $room = Wait-KitCondition { Find-KitHostedServer 'Kit Export' } 20 'the disposable Hosting room'
                Wait-KitCondition { (Request-KitServer $room.Port 'getstatus Kit') -match 'Kit Export' } 45 'the Hosting room to start' | Out-Null
                $dedicated = Get-Binary 'original' -Server -Root $InstallRoot
                $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($run.Process.Id)" |
                    Where-Object { $_.ExecutablePath -ieq $dedicated -and $_.CommandLine -like "*$($room.Id)*" } |
                    Select-Object -First 1
                if ($child) {
                    $candidate = [Diagnostics.Process]::GetProcessById($child.ProcessId)
                    $null = $candidate.Handle
                    if ($candidate.Path -ieq $dedicated -and
                        [Math]::Abs(($candidate.StartTime - $child.CreationDate).TotalMilliseconds) -lt 1) {
                        $hostedProcess = $candidate
                    } else { $candidate.Dispose() }
                }
                if (-not $hostedProcess) { throw 'The Hosting picture check could not verify its own server process.' }
                $export = 'Export for another machine' + [char]0x2026
                $row = Wait-KitCondition { Find-KitControl $app 'Kit Export' -Name } 12 'the saved Hosting row'
                $row.SetFocus()
                [KitDesktop]::Chord(16, 121)
                Save-KitSurface $app 'hosting-folder' @($export,'Show its folder in File Explorer')
                [KitDesktop]::Tap(27)
                Invoke-KitControl $app $export -Name
                Save-KitSurface $app 'hosting-export' @('Cancel')
                Invoke-KitControl $app 'Cancel' -Name
                Wait-KitCondition { -not (Find-KitControl $app 'Cancel' -Name) } 12 'the export dialog to close' | Out-Null
            }
            'configs' {
                Invoke-KitControl $app 'ConfigsItem'
                Invoke-KitControl $app 'Binds' -Name
                Save-KitSurface $app 'config-binds' @('Find an action or key')
                Invoke-KitControl $app 'History' -Name
                Save-KitSurface $app 'config-history' @('Save a snapshot now')
                Invoke-KitControl $app 'Check a config' -Name
                Save-KitSurface $app 'config-checker' @('Check a loadout')
            }
            'library' {
                Invoke-KitControl $app 'LibraryItem'
                Invoke-KitControl $app 'MatchesSection'
                Save-KitSurface $app 'library-matches' @('Match history',('Toujane ' + [char]0x00b7 + ' Deathmatch'))
                Invoke-KitControl $app 'DemosSection'
                Save-KitSurface $app 'library-demos' @('Find a file','Show in File Explorer')
                Invoke-KitControl $app 'ScreenshotsSection'
                Save-KitSurface $app 'library-screenshots' @('Find a file','Show in File Explorer')
            }
            'settings' {
                Invoke-KitControl $app 'Settings' -Name
                Invoke-KitControl $app 'Display' -Name
                Save-KitSurface $app 'settings-display' @('Display','Mode','Graphics')
                Invoke-KitControl $app 'About' -Name
                $report = 'Report a problem' + [char]0x2026
                Save-KitSurface $app 'settings-about' @($report,'Acknowledgements')
                Invoke-KitControl $app $report -Name
                Save-KitSurface $app 'problem-report' @('Copy details','Cancel')
                Invoke-KitControl $app 'Cancel' -Name
            }
        }
    } finally {
        if ($Page -eq 'hosting') {
            if ($hostedProcess) {
                try {
                    # This step checks the export and folder controls. The
                    # separate Hosting match step proves graceful shutdown.
                    if (-not $hostedProcess.HasExited) {
                        $hostedProcess.Kill()
                        if (-not $hostedProcess.WaitForExit(5000)) { throw 'The owned picture fixture server did not exit.' }
                    }
                } finally { $hostedProcess.Dispose() }
            }
            $logs = Join-Path $LauncherHome 'hosting\servers'
            foreach ($file in Get-ChildItem -LiteralPath $logs -Recurse -File -Filter 'engine-*.log' -ErrorAction SilentlyContinue) {
                Save-KitEngineAudit (Read-KitText $file.FullName) $Results
                Save-Text $file.FullName (Join-Path $Results $file.Name)
            }
        }
        Stop-KitLauncher $run $Results
    }
}
