function Test-KitFiles {
    $failed = @()
    foreach ($line in Get-Content -LiteralPath (Join-Path $Kit 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'The kit checksum list is damaged.' }
        $expected = $Matches[1]
        $file = $Matches[2]
        $path = Join-Path $Kit $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $expected) {
            $failed += $file
        }
    }
    if ($failed.Count) { throw "Copy the kit again. These files are missing or different: $($failed -join ', ')." }
    'All kit checksums matched.' | Set-Content -LiteralPath (Join-Path $Results 'kit-checksums.txt')
}

function Save-KitPreflightComputer {
    $version = Get-KitWindowsVersion
    $now = [DateTimeOffset]::UtcNow
    $clock = if ($DriverUtc) {
        $delta = [math]::Round(($now - [DateTimeOffset]::Parse($DriverUtc)).TotalSeconds)
        if ([math]::Abs($delta) -gt 300) { Write-Warning 'Windows and the Mac differ by over five minutes. Check the Windows date and time.' }
        "Windows clock relative to Mac: $delta seconds."
    } else {
        $stamp = Select-String -LiteralPath (Join-Path $Kit 'BUILD.txt') -Pattern '^Kit date (\d{4})-' | Select-Object -First 1
        if ($stamp -and $now.Year -lt [int]$stamp.Matches[0].Groups[1].Value) {
            Write-Warning 'The Windows clock predates this kit. Check the Windows date and time before trusting signatures.'
            'Windows clock predates this kit.'
        } else { 'Windows clock recorded.' }
    }
    $enough = $version.BuildNumber -ge 19041
    @("$($version.Family) $($version.Release) $($version.Edition), build $($version.Build)",
        "Windows PowerShell $($PSVersionTable.PSVersion); launcher Windows build floor 19041 met: $enough",
        "Windows UTC $($now.ToString('o')); $clock", "Graphics run label: $GraphicsLabel") |
        Set-Content -LiteralPath (Join-Path $Results 'windows-version.txt') -Encoding UTF8
    if (-not $enough) { throw 'This Windows version is too old for the launcher. Run Windows Update and restart, then use a new run name. Build 19041 or later is required.' }
    Initialize-KitDesktop
    $context = [KitDesktop]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try {
        foreach ($screen in [Windows.Forms.Screen]::AllScreens) {
            $dpi = [KitDesktop]::DisplayDpi($screen.Bounds.Left, $screen.Bounds.Top)
            $scale = if ($dpi -match '^(\d+)x') { "$([math]::Round([int]$Matches[1] * 100 / 96))%" } else { 'unavailable' }
            "$($screen.DeviceName) $($screen.Bounds) primary=$($screen.Primary) DPI=$dpi scaling=$scale"
        }
    } finally {
        if ($context -ne [IntPtr]::Zero) { [void][KitDesktop]::SetThreadDpiAwarenessContext($context) }
    }
}

function Save-KitGraphicsPreferences([string]$Root, [string]$Evidence) {
    $key = 'HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences'
    $data = foreach ($rel in 'launcher\CoD2Unbound.Windows.exe','original\cod2_win32.exe','cod2x\cod2_win32.exe',
        'original\cod2_win32_ded.exe','cod2x\cod2_win32_ded.exe') {
        $path = Join-Path $Root $rel
        # Windows PowerShell 5.1 throws for a missing named registry value.
        $value = [Microsoft.Win32.Registry]::GetValue($key, $path, $null)
        [pscustomobject]@{ Program=$rel; Preference=if ($value) { $value } else { 'System default' } }
    }
    $gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
    @{ Adapters=$gpu; Programs=@($data); RunLabel=$GraphicsLabel } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $Evidence 'graphics-preferences.json') -Encoding UTF8
}

function Save-Signatures([string[]]$Paths, [string]$Destination) {
    $signed = foreach ($path in $Paths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        [pscustomobject]@{
            File = if ($path.StartsWith($Kit, [StringComparison]::OrdinalIgnoreCase)) {
                $path.Substring($Kit.Length + 1)
            } else { Split-Path $path -Leaf }
            Status = $signature.Status.ToString()
            Signed = $null -ne $signature.SignerCertificate
            Timestamped = $null -ne $signature.TimeStamperCertificate
        }
    }
    @($signed) | ConvertTo-Json | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Save-Machine {
    $log = New-RunContext 'computer'
    $version = Get-KitWindowsVersion
    $gpu = @(Get-CimInstance -ClassName Win32_VideoController |
        Select-Object Name, AdapterRAM, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate)
    [ordered]@{ Windows = $version.Release; Family = $version.Family; Edition = $version.Edition; Build = $version.Build
        Graphics = $gpu; PowerShell = $PSVersionTable.PSVersion.ToString() } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $log.Evidence 'windows-and-graphics.json') -Encoding UTF8
    $raw = Join-Path $log.Folder 'dxdiag.txt'
    $p = Start-Process -FilePath "$env:SystemRoot\System32\dxdiag.exe" -WindowStyle Hidden -ArgumentList "/whql:off /t `"$raw`"" -PassThru
    try {
        # DxDiag may spawn a second process and return before the report is written.
        $deadline = (Get-Date).AddSeconds(75)
        do {
            Start-Sleep -Seconds 2
            if (Test-Path -LiteralPath $raw) {
                $length = (Get-Item -LiteralPath $raw).Length
                Start-Sleep -Seconds 1
                if ($length -gt 0 -and (Get-Item -LiteralPath $raw).Length -eq $length) {
                    Save-Text $raw (Join-Path $log.Evidence 'dxdiag.txt')
                    break
                }
            }
        } while ((Get-Date) -lt $deadline)
        if (-not (Test-Path -LiteralPath (Join-Path $log.Evidence 'dxdiag.txt'))) {
            Write-Warning 'DxDiag did not finish. Run dxdiag and use Save All Information if needed.'
        }
    } finally { $p.Dispose() }

    try {
        Get-MpComputerStatus | Select-Object AntivirusEnabled, RealTimeProtectionEnabled, SmartAppControlState |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $log.Evidence 'windows-security.json') -Encoding UTF8
    } catch { Write-Warning 'Record the Windows Security settings on the checklist.' }
    $setup = @(Get-ChildItem -LiteralPath (Join-Path $Kit 'install') -Recurse -File -Filter 'CoD2-Unbound-*' |
        Where-Object Extension -in '.exe','.cat')
    $portable = @(Get-ChildItem -LiteralPath (Join-Path $Kit 'portable') -Recurse -File -Filter 'cod2_win32*.exe')
    $probes = @(Get-ChildItem -LiteralPath (Join-Path $Kit 'probes') -Recurse -File |
        Where-Object Extension -in '.exe','.dll')
    Save-Signatures ($setup.FullName + $portable.FullName + $probes.FullName) (Join-Path $log.Evidence 'signatures.json')
    $zip = Get-ChildItem -LiteralPath (Join-Path $Kit 'install') -File -Filter 'CoD2-Unbound-*-server.zip'
    $cat = [IO.Path]::ChangeExtension($zip.FullName, '.cat')
    $hash = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash
    $dump = & "$env:SystemRoot\System32\certutil.exe" -dump $cat 2>&1
    $dump | Set-Content -LiteralPath (Join-Path $log.Folder 'catalogue-raw.txt') -Encoding UTF8
    "ZIP SHA-256 $hash", "Catalogue member digest seen by certutil: $(($dump -join '' -replace '\s','') -imatch $hash)" |
        Set-Content -LiteralPath (Join-Path $log.Evidence 'server-catalogue.txt')
}

function Get-TreeHashes([string]$Root, [string]$Relative = '') {
    $folder = if ($Relative) { Join-Path $Root $Relative } else { $Root }
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $folder -Force | Sort-Object Name) {
        $name = if ($Relative) { Join-Path $Relative $item.Name } else { $item.Name }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            "Link  $name"
        } elseif ($item.PSIsContainer) {
            Get-TreeHashes $Root $name
        } else {
            '{0}  {1}' -f (Get-FileHash -LiteralPath $item.FullName).Hash, $name
        }
    }
}

function Get-KitArchiveInventory([string]$Root) {
    $main = Join-Path $Root 'main'
    foreach ($item in Get-ChildItem -LiteralPath $main -File -Filter '*.iwd' | Sort-Object Name) {
        '{0}  main/{1}' -f $item.Length, $item.Name
    }
}

function Save-InstallInventory([string]$GamePath, [string]$RetailPath, [string]$Phase) {
    $roots = [ordered]@{ 'selected-game' = $GamePath; 'retail-game' = $RetailPath }
    foreach ($name in $roots.Keys) {
        if ($roots[$name] -and ($name -ne 'retail-game' -or $RetailPath -ine $GamePath)) {
            $inventory = @("Game data: $($roots[$name])") + @(Get-KitArchiveInventory $roots[$name])
            Protect-KitText ($inventory -join "`r`n") |
                Set-Content -LiteralPath (Join-Path $Results "$name-$Phase.txt") -Encoding UTF8
        }
    }
    if ($Phase -eq 'after') {
        foreach ($name in $roots.Keys) {
            $before = Join-Path $Results "$name-before.txt"
            if (-not (Test-Path -LiteralPath $before)) { continue }
            $after = Join-Path $Results "$name-after.txt"
            $same = (Get-FileHash -LiteralPath $before).Hash -eq (Get-FileHash -LiteralPath $after).Hash
            "$name unchanged: $same"
        }
    }
}
