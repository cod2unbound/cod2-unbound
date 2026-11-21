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
    if ($failed.Count) { throw "Copy the kit again. SHA-256 failed for $($failed -join ', ')." }
    'All kit checksums matched.' | Set-Content -LiteralPath (Join-Path $Results 'kit-checksums.txt')
}

function Save-Signatures([string[]]$Paths, [string]$Destination) {
    $signed = foreach ($path in $Paths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        [pscustomobject]@{
            File = Split-Path $path -Leaf
            Status = $signature.Status.ToString()
            Signed = $null -ne $signature.SignerCertificate
            Timestamped = $null -ne $signature.TimeStamperCertificate
        }
    }
    @($signed) | ConvertTo-Json | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Save-Machine {
    $log = New-RunContext 'computer'
    $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $gpu = @(Get-CimInstance -ClassName Win32_VideoController |
        Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution)
    [ordered]@{ Windows = $version.DisplayVersion; Build = "$($version.CurrentBuildNumber).$($version.UBR)"
        Graphics = $gpu; PowerShell = $PSVersionTable.PSVersion.ToString() } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $log.Evidence 'windows-and-graphics.json') -Encoding UTF8
    $raw = Join-Path $log.Folder 'dxdiag.txt'
    $p = Start-Process -FilePath "$env:SystemRoot\System32\dxdiag.exe" -ArgumentList "/whql:off /t `"$raw`"" -PassThru
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
    $setup = @(Get-ChildItem -LiteralPath (Join-Path $Kit 'install') -Recurse -File | Where-Object Extension -in '.exe','.cat')
    $portable = @(Get-ChildItem -LiteralPath (Join-Path $Kit 'portable') -Recurse -File -Filter '*.exe')
    Save-Signatures ($setup.FullName + $portable.FullName) (Join-Path $log.Evidence 'signatures.json')
    $zip = Get-ChildItem -LiteralPath (Join-Path $Kit 'install') -File -Filter '*-server.zip'
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

function Save-InstallHashes([string]$GamePath, [string]$RetailPath, [string]$Phase) {
    $roots = [ordered]@{ 'selected-game' = $GamePath; 'retail-game' = $RetailPath
        'virtual-store' = (Join-Path $LocalData 'VirtualStore') }
    foreach ($name in $roots.Keys) {
        if ($roots[$name] -and ($name -ne 'retail-game' -or $RetailPath -ine $GamePath)) {
            @(Get-TreeHashes $roots[$name]) |
                Set-Content -LiteralPath (Join-Path $Private "$name-$Phase.txt") -Encoding UTF8
        }
    }
    if ($Phase -eq 'after') {
        foreach ($name in $roots.Keys) {
            $before = Join-Path $Private "$name-before.txt"
            if (-not (Test-Path -LiteralPath $before)) { continue }
            $after = Join-Path $Private "$name-after.txt"
            $same = (Get-FileHash -LiteralPath $before).Hash -eq (Get-FileHash -LiteralPath $after).Hash
            "$name unchanged: $same"
        }
    }
}
