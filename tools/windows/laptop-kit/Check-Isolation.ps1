function Get-PublicRegistryTree([string]$Path) {
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if (-not $key) { return }
    try {
        $Path
        foreach ($name in ($key.GetValueNames() | Sort-Object)) {
            [pscustomobject]@{ Name = $name; Kind = $key.GetValueKind($name).ToString()
                Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
        }
        foreach ($child in ($key.GetSubKeyNames() | Sort-Object)) {
            Get-PublicRegistryTree (Join-Path $Path $child)
        }
    } finally { $key.Dispose() }
}

function Get-KitPublicInstallState {
    $uninstall = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CoD2Unbound_is1'
    $classes = 'CoD2Unbound.Demo','CoD2Unbound.Config','.dm_1','.cfg','cod2','cod2x','cod2unbound'
    $installed = foreach ($scope in 'HKCU','HKLM') {
        $path = "${Scope}:\$uninstall"
        $location = (Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue).InstallLocation
        $default = if ($scope -eq 'HKCU') { Join-Path $LocalData 'Programs\CoD2 Unbound' }
            else { Join-Path $env:ProgramFiles 'CoD2 Unbound' }
        $root = if ($location) { $location } else { $default }
        $startKind = if ($scope -eq 'HKCU') { 'Programs' } else { 'CommonPrograms' }
        $start = Join-Path ([Environment]::GetFolderPath($startKind)) 'CoD2 Unbound'
        $versionFile = Join-Path $root 'RELEASE_VERSION'
        $version = if (Test-Path -LiteralPath $versionFile -PathType Leaf) { [IO.File]::ReadAllText($versionFile) }
        $present = Test-Path -LiteralPath $root -PathType Container
        $files = if ($present) {
            Get-ChildItem -LiteralPath $root -Recurse -File -Force |
                ForEach-Object { $_.FullName.Substring($root.TrimEnd('\').Length + 1) } | Sort-Object
        }
        [ordered]@{ Scope = $scope; Registration = @(Get-PublicRegistryTree $path)
            Present = $present
            Version = $version
            Files = @($files)
            StartPresent = (Test-Path -LiteralPath $start)
            Shortcuts = @(Get-TreeHashes $start)
            Classes = @(foreach ($class in $classes) { Get-PublicRegistryTree "${Scope}:\Software\Classes\$class" })
        }
    }
    [ordered]@{
        Installed = @($installed)
        UserChoices = @(foreach ($class in '.dm_1','.cfg') {
            Get-PublicRegistryTree "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$class\UserChoice"
        })
    } | ConvertTo-Json -Depth 12
}

function Save-KitPublicInstallState([string]$Stage) {
    $state = Get-KitPublicInstallState
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($state))) }
    finally { $sha.Dispose() }
    $baseline = Join-Path $KitSafety 'public-install-before.json'
    $fingerprint = Join-Path $KitSafety 'public-install-before.sha256'
    if ($Stage -eq 'before') {
        if (-not (Test-Path -LiteralPath $fingerprint)) {
            [IO.File]::WriteAllText($baseline, (Protect-KitText $state))
            [IO.File]::WriteAllText($fingerprint, $digest)
        }
    } else {
        [IO.File]::WriteAllText((Join-Path $Results 'public-install-after.json'), (Protect-KitText $state))
        if ([IO.File]::ReadAllText($fingerprint) -cne $digest) {
            throw 'The Public installation files, version, shortcuts or registry changed during the kit run.'
        }
    }
}

function Assert-FixtureSetup([string]$Path) {
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if ($info.ProductName.TrimEnd() -cne $FixtureName -or $info.FileDescription.TrimEnd() -cne "$FixtureName Setup with recovery") {
        throw 'This installer does not have the separate CoD2 Unbound Test identity. Rebuild both test versions.'
    }
}

function Assert-KitSlotBinaries {
    $binaries = @((Join-Path $Kit 'probes\test-key.exe'))
    foreach ($edition in 'original','cod2x') {
        $binaries += Get-Binary $edition
        $binaries += Get-Binary $edition -Server
    }
    foreach ($exe in $binaries) {
        $binary = [IO.File]::ReadAllBytes($exe)
        if (-not [Text.Encoding]::Unicode.GetString($binary).Contains('COD2_UNBOUND_TEST_KEY_SLOT')) {
            throw "Rebuild the kit. $(Split-Path $exe -Leaf) does not support the isolated test key slot."
        }
    }
}

function Assert-NoKitInstall {
    foreach ($scope in 'HKCU','HKLM') {
        foreach ($path in $FixtureUninstall, 'Software\CoD2UnboundTest\InstallTransaction',
            "Software\Classes\$FixtureDemo", 'Software\Classes\CoD2UnboundTest.Config',
            "Software\Classes\$FixtureDemoExtension", 'Software\Classes\.cod2-unbound-test-config',
            'Software\Classes\cod2-test', 'Software\Classes\cod2x-test', 'Software\Classes\cod2unbound-test') {
            if (Test-Path -LiteralPath "${Scope}:\$path") { throw "A test registration remains or already exists: $path." }
        }
    }
    foreach ($root in $InstallRoot, (Join-Path $env:ProgramFiles $FixtureName)) {
        $parent = Split-Path -Parent $root
        if (Get-ChildItem -LiteralPath $parent -Filter "$FixtureName*" -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $FixtureName -or $_.Name -like "$FixtureName.*-install*" }) {
            throw 'A CoD2 Unbound Test installation or recovery folder remains or already exists.'
        }
    }
    foreach ($kind in 'Programs','CommonPrograms') {
        if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath($kind)) $FixtureName)) {
            throw 'A CoD2 Unbound Test Start group remains or already exists.'
        }
    }
    if (Test-Path -LiteralPath (Join-Path $AppDataRoot 'Fixture')) {
        throw 'A CoD2 Unbound Test settings folder remains or already exists.'
    }
}

function Invoke-KitEmptySlotClients {
    foreach ($edition in 'original','cod2x') {
        $context = New-RunContext "empty-slot-$edition"
        $arguments = (Get-EngineArgs $context.Folder $GamePath) +
            ' +set net_noudp 1 +set r_fullscreen 0 +set r_mode 800x600 +set com_introPlayed 1 +quit'
        $run = Start-KitProcess (Get-Binary $edition) $arguments $context.Folder @{ COD2_CRASH_NOPOPUP = '1' }
        $done = Complete-KitProcess $run $context.Evidence 90
        $stderr = Join-Path $context.Evidence 'stderr.txt'
        if ($done.TimedOut -or $done.ExitCode -ne 0 -or
            -not (Select-String -LiteralPath $stderr -SimpleMatch 'CoD2 Unbound: no CD key in the test slot.' -Quiet)) {
            throw "$edition did not report an empty CD key slot."
        }
        if ($edition -eq 'original') { Wait-KitCooling }
    }
}
