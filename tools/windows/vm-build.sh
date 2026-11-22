#!/bin/sh
# Run the portable tests and publish the committed launcher on the running test VM.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
out="${COD2_WINDOWS_VM_OUTPUT:-$root/output/r05/winvm}"
export_x64=0
if [ "$#" -ne 0 ]; then
    if [ "$#" -ne 1 ] || [ "$1" != --export-x64 ]; then
        echo "Usage: $0 [--export-x64]" >&2
        exit 2
    fi
    export_x64=1
fi
mkdir -p "$out"

bounded() {
    /usr/bin/perl -e 'alarm shift; exec @ARGV' "$@"
}

guest() {
    require_utm || return
    bounded "$(($1 + 60))" "/Users/stix/Library/Containers/com.utmapp.UTM/Data/Documents/win11-assets/tools/gx.sh" "$@"
}

utm() {
    require_utm || return
    limit=$1
    shift
    bounded "$limit" /Applications/UTM.app/Contents/MacOS/utmctl "$@"
}

require_utm() {
    if ! /usr/bin/pgrep -f '^/Applications/UTM\.app/Contents/MacOS/UTM($| )' >/dev/null; then
        echo "UTM is closed. Run this script with cod2-win11 already running." >&2
        return 1
    fi
}

check_disk() {
    /bin/df -h /System/Volumes/Data
    free=$(/bin/df -Pk /System/Volumes/Data | awk 'NR==2 { print $4 }')
    if [ "$free" -lt 8388608 ]; then
        echo "At least 8 GiB must be free on the Mac before exporting or building." >&2
        exit 1
    fi
}

check_disk
if ! status=$(utm 12 status --hide cod2-win11) || [ "$status" != started ]; then
    echo "Could not confirm that cod2-win11 is running. Check UTM and the guest agent." >&2
    exit 1
fi

scratch=$(mktemp -d "$out/vm.XXXXXXXX")
vmroot="C:\\$(basename "$scratch")"
guest_workspace_requested=0

cleanup() {
    result=$?
    trap - EXIT HUP INT TERM
    if [ "$guest_workspace_requested" -eq 1 ]; then
        if guest 60 "\$ErrorActionPreference = 'Stop'; if (Test-Path -LiteralPath '$vmroot') { Remove-Item -LiteralPath '$vmroot' -Recurse }; Set-Content -LiteralPath '$vmroot.cleaned' -Value cleaned -Encoding ascii; exit 0" > "$scratch/guest-cleanup.log" 2>&1 &&
            utm 12 file pull --hide cod2-win11 "$vmroot.cleaned" > "$scratch/receipt" 2>> "$scratch/guest-cleanup.log" &&
            [ "$(tr -d '\r\n' < "$scratch/receipt")" = cleaned ]; then
            guest 15 "Remove-Item -LiteralPath '$vmroot.cleaned' -ErrorAction Stop; exit 0" >> "$scratch/guest-cleanup.log" 2>&1 || result=1
        else
            echo "Could not confirm guest workspace cleanup. See $scratch/guest-cleanup.log." >&2
            result=1
        fi
    fi
    for file in "$scratch/source.zip" "$scratch/build.ps1" "$scratch/receipt" "$scratch/launcher-win-x64.zip.partial"; do
        if [ -f "$file" ]; then
            rm "$file" || result=1
        fi
    done
    echo "Evidence is in $scratch"
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

revision=$(git -C "$root" rev-parse HEAD)
printf '%s\n' "$revision" > "$scratch/revision.txt"
git -C "$root" archive --format=zip --output="$scratch/source.zip" "$revision" \
    dotnet launcher/Resources tests/launcher/fixtures/core-parity docs/brand/monogram.svg

cat > "$scratch/build.ps1" <<'POWERSHELL'
param(
    [ValidateSet('core', 'shell')][string]$Phase,
    [string]$ExportRevision
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$work = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $work 'cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:UseSharedCompilation = 'false'
$env:NUGET_PACKAGES = Join-Path $work 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
$source = 'https://api.nuget.org/v3/index.json'
$dotnet = Join-Path $work 'sdk\dotnet.exe'

function Invoke-DotNet {
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited $LASTEXITCODE" }
}

function Record-Download($address, $file) {
    [pscustomobject]@{
        URL = $address
        SHA256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        Bytes = (Get-Item -LiteralPath $file).Length
    } | Export-Csv -Path (Join-Path $work 'downloads.csv') -Append -NoTypeInformation
}

$code = 0
try {
    if ($Phase -eq 'core') {
        $sdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-arm64.zip'
        $sdkSha512 = '55c041098c709c898d416eaea9a56ab1d17ee884167b2093a5b3d68935a283d15ea796a880b9cf281baccdab56eae1537fae91627f480a68dec081734f535cf5'
        $sdkZip = Join-Path $work 'sdk.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $sdkUrl -OutFile $sdkZip -TimeoutSec 300
        Record-Download $sdkUrl $sdkZip
        if ((Get-FileHash -LiteralPath $sdkZip -Algorithm SHA512).Hash -ne $sdkSha512) {
            throw 'SDK SHA-512 differs from the Microsoft release metadata.'
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($sdkZip, (Join-Path $work 'sdk'))
        Remove-Item -LiteralPath $sdkZip
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $work 'source.zip'), (Join-Path $work 'src'))
        Remove-Item -LiteralPath (Join-Path $work 'source.zip')
        Set-Location (Join-Path $work 'src\dotnet')
        Invoke-DotNet --info
        Invoke-DotNet restore CoD2Unbound.sln --locked-mode --source $source '-p:NuGetAudit=false' '-m:4'
        Invoke-DotNet build CoD2Unbound.sln --no-restore -c Release '-m:4'
        Invoke-DotNet run --project CoD2Unbound.Core.Tests -c Release --no-build
    } else {
        Set-Location (Join-Path $work 'src\dotnet')
        $project = 'CoD2Unbound.Windows\CoD2Unbound.Windows.csproj'
        foreach ($platform in 'ARM64', 'x64') {
            $rid = "win-$($platform.ToLowerInvariant())"
            $output = Join-Path $work $rid
            Invoke-DotNet restore $project -r $rid "-p:Platform=$platform" --source $source '-p:NuGetAudit=false' '-m:4'
            Invoke-DotNet publish $project --no-restore -c Release -r $rid "-p:Platform=$platform" -o $output '-m:4'
            foreach ($name in 'CoD2Unbound.Windows.exe', 'CoD2Unbound.Windows.pri') {
                if (!(Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf)) {
                    throw "Missing $rid launcher file: $name"
                }
            }
            $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
            Write-Output "$rid publish contains $($files.Count) files and $(($files | Measure-Object Length -Sum).Sum) bytes."
            if ($rid -eq 'win-x64' -and $ExportRevision) {
                Set-Content -LiteralPath (Join-Path $output 'SOURCE_REVISION') -Value $ExportRevision -Encoding ascii
                @($files + (Get-Item -LiteralPath (Join-Path $output 'SOURCE_REVISION'))) |
                    Sort-Object FullName | ForEach-Object {
                        $digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                        $relative = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
                        "$digest  $relative"
                    } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding ascii
                Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
                $bundle = Join-Path $work 'launcher-win-x64.zip'
                $zip = [IO.Compression.ZipFile]::Open($bundle, [IO.Compression.ZipArchiveMode]::Create)
                try {
                    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
                        $relative = $file.FullName.Substring($output.Length + 1).Replace('\', '/')
                        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative)
                    }
                } finally {
                    $zip.Dispose()
                }
                (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant() |
                    Set-Content -LiteralPath "$bundle.sha256" -Encoding ascii
            }
        }
    }
} catch {
    Write-Output $_
    $code = 1
} finally {
    if ($Phase -eq 'shell' -or $code -ne 0) {
        Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Recurse -Filter '*.nupkg' -File -ErrorAction SilentlyContinue |
            ForEach-Object {
                $version = $_.Directory.Name.ToLowerInvariant()
                $id = $_.Directory.Parent.Name.ToLowerInvariant()
                $address = "https://api.nuget.org/v3-flatcontainer/$id/$version/$($_.Name.ToLowerInvariant())"
                Record-Download $address $_.FullName
            }
    }
}
if ($code -eq 0) { Set-Content -LiteralPath (Join-Path $work "$Phase.ok") -Value $Phase -Encoding ascii }
exit $code
POWERSHELL

guest_workspace_requested=1
ready=0
# A guest-written receipt carries success across gx.sh and utmctl.
for attempt in 1 2 3; do
    guest 25 "\$ErrorActionPreference = 'Stop'; if (!(Test-Path -LiteralPath '$vmroot')) { New-Item -Path '$vmroot' -ItemType Directory | Out-Null }; Set-Content -LiteralPath '$vmroot\ready' -Value ready -Encoding ascii" >> "$scratch/guest-ready.log" 2>&1 || :
    if utm 12 file pull --hide cod2-win11 "$vmroot\ready" > "$scratch/receipt" &&
        [ "$(tr -d '\r\n' < "$scratch/receipt")" = ready ]; then
        ready=1
        break
    fi
    sleep 8
done
if [ "$ready" -ne 1 ]; then
    echo "The cod2-win11 guest agent did not confirm workspace setup. See $scratch/guest-ready.log." >&2
    exit 1
fi

utm 210 file push --hide cod2-win11 "$vmroot\source.zip" < "$scratch/source.zip"
utm 20 file push --hide cod2-win11 "$vmroot\build.ps1" < "$scratch/build.ps1"

run_phase() {
    phase=$1
    seconds=$2
    check_disk
    export_arg=
    if [ "$phase" = shell ] && [ "$export_x64" -eq 1 ]; then
        export_arg="-ExportRevision '$revision'"
    fi
    guest "$seconds" "& '$vmroot\build.ps1' -Phase '$phase' $export_arg; exit \$LASTEXITCODE" > "$scratch/$phase.log" 2>&1 || :
    utm 20 file pull --hide cod2-win11 "$vmroot\downloads.csv" > "$scratch/downloads.csv" || :
    if ! utm 12 file pull --hide cod2-win11 "$vmroot\\$phase.ok" > "$scratch/receipt" ||
        [ "$(tr -d '\r\n' < "$scratch/receipt")" != "$phase" ]; then
        echo "Windows $phase phase failed. See $scratch/$phase.log." >&2
        return 1
    fi
}

run_phase core 660
run_phase shell 900
if [ "$export_x64" -eq 1 ]; then
    bundle=launcher-win-x64.zip
    check_disk
    utm 900 file pull --hide cod2-win11 "$vmroot\\$bundle" > "$scratch/$bundle.partial"
    utm 20 file pull --hide cod2-win11 "$vmroot\\$bundle.sha256" > "$scratch/$bundle.sha256"
    expected=$(tr -d '\r\n' < "$scratch/$bundle.sha256")
    actual=$(shasum -a 256 "$scratch/$bundle.partial" | awk '{ print $1 }')
    if [ "$actual" != "$expected" ]; then
        echo "Windows and Mac SHA-256 differ for $bundle." >&2
        exit 1
    fi
    mv "$scratch/$bundle.partial" "$scratch/$bundle"
    echo "Pass $scratch/$bundle to tools/windows/release.sh."
fi
