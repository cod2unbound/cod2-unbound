function Get-KitReferenceHwid {
    # Field order, video selection and volume fallback from upstream v1.4.6.8
    # src/mss32/hwid.cpp. The UUID is required input but is not in the description.
    $bios = Get-CimInstance Win32_BIOS -OperationTimeoutSec 6 | Select-Object -First 1
    $cpu = Get-CimInstance Win32_Processor -OperationTimeoutSec 6 | Select-Object -First 1
    $board = Get-CimInstance Win32_BaseBoard -OperationTimeoutSec 6 | Select-Object -First 1
    $product = Get-CimInstance Win32_ComputerSystemProduct -OperationTimeoutSec 6 | Select-Object -First 1
    $volume = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'" -OperationTimeoutSec 6
    $part = Get-CimAssociatedInstance -InputObject $volume -Association Win32_LogicalDiskToPartition -OperationTimeoutSec 6 | Select-Object -First 1
    $disk = Get-CimAssociatedInstance -InputObject $part -Association Win32_DiskDriveToDiskPartition -OperationTimeoutSec 6 | Select-Object -First 1
    $discrete = 'NVIDIA|GEFORCE|RTX|GTX|QUADRO|FIREPRO|RADEON PRO|RADEON RX|RADEON R[987] |INTEL ARC'
    $integrated = '^INTEL\(R\) (?:HD GRAPHICS|UHD GRAPHICS|IRIS(?: PLUS| XE)? GRAPHICS|GRAPHICS|GMA)|^AMD RADEON(?:\(TM\))? (?:GRAPHICS|VEGA|R[2-8] GRAPHICS)|^Microsoft Basic Display Adapter$'
    $gpu = Get-CimInstance -Query 'SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController' -OperationTimeoutSec 6 |
        Where-Object { $_.AdapterRAM -gt 100MB -and $_.PNPDeviceID -cmatch 'PCI' -and
            ($_.Name -match $discrete -or $_.Name -notmatch $integrated) } | Select-Object -First 1
    $serial = if ($disk.SerialNumber) { $disk.SerialNumber }
        elseif ($volume.VolumeSerialNumber) { [Convert]::ToUInt32($volume.VolumeSerialNumber, 16).ToString() }
        else { 'UnknownSerial' }
    if ((@($bios.Manufacturer,$bios.SerialNumber,$cpu.Name,$cpu.ProcessorId,$board.Manufacturer,$board.SerialNumber,
            $product.UUID,$disk.Model,$serial) | Where-Object { [string]::IsNullOrEmpty($_) }).Count -ge 3) {
        throw 'Too many required hardware fields are missing for the CoD2x 1.4.6.8 comparison.'
    }
    if (-not ('KitHwid' -as [type])) { Add-Type -Path (Join-Path $Kit 'KitHwid.cs') }
    $fields = @("BIOS manufacturer: $($bios.Manufacturer)", "BIOS serial: $($bios.SerialNumber)",
        "CPU name: $($cpu.Name)", "CPU ID: $($cpu.ProcessorId)", "Motherboard manufacturer: $($board.Manufacturer)",
        "Motherboard serial: $($board.SerialNumber)", "Disk Model: $($disk.Model)", "Disk Serial: $serial", "GPU Name: $($gpu.Name)")
    $bytes = [Text.Encoding]::Default.GetBytes($fields -join "`n")
    try {
        # The upstream description buffer reserves a byte for the terminator.
        if ($bytes.Length -gt 1023) { throw 'The reference machine description does not fit the 1.4.6.8 buffer.' }
        [KitHwid]::Hash($bytes)
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function Invoke-KitHwidCheck([string]$Binary, [string]$DataPath) {
    Initialize-KitDesktop
    $reference = Get-KitReferenceHwid
    $homePath = New-Folder (Join-Path $Private 'identity-home')
    New-TestProfile $homePath
    $log = Join-Path $homePath 'main\console_mp.log'
    $argsText = (Get-EngineArgs $homePath $DataPath) +
        ' +set net_noudp 1 +set r_fullscreen 0 +set r_mode 800x600 +set com_introPlayed 1 +set r_aaSamples 1 +cl_hwid2 +quit'
    $run = Start-KitProcess $Binary $argsText $homePath
    try {
        if (-not $run.Process.WaitForExit(90000)) { throw 'The offline CoD2x identity probe did not exit.' }
        $output = $run.Output.GetAwaiter().GetResult()
        if (Test-Path -LiteralPath $log) { $output += "`n" + (Read-KitText $log) }
        $match = [regex]::Match($output, '(?im)^\s*"cl_hwid2"\s+is:\s*"([a-f0-9]{32})\^7"')
        if ($run.Process.ExitCode -ne 0 -or -not $match.Success) {
            throw "The offline CoD2x identity probe returned no usable identifier (exit $($run.Process.ExitCode))."
        }
        if ($match.Groups[1].Value -cne $reference) {
            throw 'CoD2x hardware identifier differs from the 1.4.6.8 reference. CoD2x online play is blocked.'
        }
        'CoD2x hardware identifier matched the 1.4.6.8 reference on this PC.' |
            Set-Content -LiteralPath (Join-Path $Results 'identity.txt') -Encoding UTF8
    } finally {
        if (-not $run.Process.HasExited) {
            $run.Process.Kill()
            [void]$run.Process.WaitForExit(5000)
        }
        $run.Process.Dispose()
        Remove-KitScratch $homePath
    }
}
