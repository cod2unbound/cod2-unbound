function New-KitFrameTrace([string]$Evidence) {
    $path = Join-Path $Evidence 'frame-times.bin'
    if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    @{ Path=$path; Start=$null; End=$null; Cap=0 }
}

function Get-KitFrameAdapter([string]$Console) {
    $match = [regex]::Matches($Console, '(?im)^(?:Using D3D9 adapter|Direct3D 9 adapter|Direct3D device|GL_RENDERER):[^\S\r\n]*(.+)$') |
        Select-Object -Last 1
    if ($match) { $match.Groups[1].Value.Trim() }
}

function Start-KitFrameSample($Trace, [IntPtr]$Window, [string]$Log) {
    try {
        Send-KitCommand $Window '/toggleconsole; com_maxfps; g_competitive'
        $text = Read-KitText $Log
        $cap = [regex]::Matches($text, '(?m)^"com_maxfps" is: "(\d+)\^7"') | Select-Object -Last 1
        if ($cap) {
            $Trace.Cap = [int]$cap.Groups[1].Value
            if ($text -match '(?m)^"g_competitive" is: "1\^7"' -and ($Trace.Cap -lt 125 -or $Trace.Cap -gt 250)) {
                $Trace.Cap = 250
            }
        }
    } catch {
        # Console input for telemetry is advisory. The gameplay check still runs.
    }
    $Trace.Start = [Diagnostics.Stopwatch]::GetTimestamp()
}

function Save-KitFrameTrace($Trace, [string]$Evidence, [string]$Log, [string]$Label) {
    if (-not $Trace) { return }
    $line = try {
        if (-not (Test-Path -LiteralPath $Trace.Path -PathType Leaf)) { throw 'the game left no trace on exit' }
        if (-not $Trace.Start -or -not $Trace.End) { throw 'no timed map interval was reached' }
        $adapter = if ($Log -and (Test-Path -LiteralPath $Log -PathType Leaf)) { Get-KitFrameAdapter (Read-KitText $Log) }
        if (-not $adapter) { $adapter = 'unreported' }
        $python = Join-Path $Kit 'frame-analyser\python.exe'
        $analyser = Join-Path $Kit 'frame-times.py'
        $message = & $python -I -X utf8 $analyser $Trace.Path --source engine --cap $Trace.Cap `
            --qpc-start $Trace.Start --qpc-end $Trace.End --receipt --adapter "$adapter" 2>&1
        if ($LASTEXITCODE -ne 0) { throw ($message -join ' ') }
        $message
    } catch {
        "Frame times unavailable ($($_.Exception.Message))"
    }
    if ($Label) { $line = "$Label $line" }
    $line = Protect-KitText $line
    $script:KitFrameLines += @($line)
    $line | Set-Content -LiteralPath (Join-Path $Evidence 'frame-times.txt') -Encoding UTF8
}

function Invoke-KitDemoFrames([string]$Edition, [string]$Renderer) {
    Stop-KitIfNoGame
    Initialize-KitDesktop
    if (-not ('KitFrames' -as [type])) {
        Add-Type -Path (Join-Path $Kit 'Compare-Frames.cs') -ReferencedAssemblies System.Drawing
    }
    $inputRoot = Join-Path $Kit "frame-references\$Edition\$Renderer"
    Stop-KitIfMissing $inputRoot 'This kit has no reference pictures for this edition and mode.'
    foreach ($map in 'mp_toujane','mp_harbor') {
        if ($map -eq 'mp_harbor') { Wait-KitCooling }
        $entry = New-RunContext $map
        $gameHome = Join-Path $entry.Folder 'profile'
        New-TestProfile $gameHome
        $demoPath = Join-Path $Kit "frame-references\$Edition\smoke-$map.dm_1"
        Copy-Item -LiteralPath $demoPath -Destination (New-Folder (Join-Path $gameHome 'main\demos'))
        $demo = "smoke-$map"
        $label = "$Renderer-$map"
        $samples = if ($map -eq 'mp_harbor') { @('00','04','08') } else { @('00') }
        $commands = @('toggleconsole',"timedemo $demo",'wait 12','cg_draw2D 0','cg_drawGun 0','viewpos')
        foreach ($sample in $samples) {
            if ($sample -ne '00') { $commands += 'wait 8' }
            $commands += "screenshot $label-$sample"
        }
        $commands | Set-Content -LiteralPath (Join-Path $gameHome 'main\kit-frames.cfg') -Encoding Ascii
        $argsText = (Get-EngineArgs $gameHome $GamePath) +
            " +set r_rendererPreference $Renderer +set r_aaSamples 1 +set net_noudp 1 +set com_introPlayed 1 +set r_fullscreen 0 +set r_mode 640x480"
        $trace = New-KitFrameTrace $entry.Evidence
        $run = Start-KitProcess (Get-Binary $Edition) $argsText $entry.Folder @{ COD2_FRAME_TRACE_FILE=$trace.Path }
        $log = Join-Path $gameHome 'main\console_mp.log'
        $window = [IntPtr]::Zero
        try {
            $window = Get-KitGameWindow $run.Process
            Wait-KitCondition {
                (Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch 'Working directory:' -Quiet)
            } 40 'the demo startup log' | Out-Null
            Start-Sleep -Seconds 3
            Start-KitFrameSample $trace $window $log
            Send-KitCommand $window '/exec kit-frames.cfg'
            foreach ($sample in $samples) {
                $basename = "$label-$sample"
                $tga = Join-Path $gameHome "main\screenshots\$basename.tga"
                Wait-KitCondition {
                    (Test-Path -LiteralPath $tga) -and (Get-Item -LiteralPath $tga).Length -ge 921618
                } 28 "demo picture $basename" | Out-Null
                Copy-Item -LiteralPath $tga -Destination $entry.Evidence
                foreach ($source in 'windows','wine') {
                    $reference = Join-Path $inputRoot "$basename-$source.png"
                    if (-not (Test-Path -LiteralPath $reference)) { continue }
                    $pair = Join-Path $entry.Evidence "$basename-$source-pair.png"
                    $metrics = [KitFrames]::Compare($tga, $reference, $pair)
                    $detail = if ($source -eq 'windows') { 'Windows software adapter' } else { 'Wine with DXVK' }
                    "$basename against $detail. $metrics. Sky, fog, water, edges and texture filtering can change with the adapter. Use the side-by-side picture for the visual verdict." |
                        Add-Content -LiteralPath (Join-Path $entry.Evidence 'comparison.txt') -Encoding UTF8
                }
            }
        } finally {
            $trace.End = [Diagnostics.Stopwatch]::GetTimestamp()
            if (-not $run.Process.HasExited -and $window -ne [IntPtr]::Zero) {
                try { Send-KitCommand $window '/quit' } catch { [void]$run.Process.CloseMainWindow() }
            }
            Complete-KitProcess $run $entry.Evidence 12 | Out-Null
            Save-GameEvidence $gameHome $entry.Evidence
            Save-KitFrameTrace $trace $entry.Evidence $log $map
        }
    }
}

function Open-KitComparison {
    $picture = Get-ChildItem -LiteralPath $RunResults -Filter '*-windows-pair.png' -File -Recurse |
        Where-Object Name -like '*toujane*' | Select-Object -First 1
    if ($picture) {
        # Use the shell's normal picture viewer in the signed-in session.
        Start-Process -FilePath $picture.FullName
    } else { throw [NotSupportedException]::new('No software-adapter comparison picture was saved.') }
}
