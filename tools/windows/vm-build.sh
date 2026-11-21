#!/bin/sh
# Build the committed launcher and run portable tests on the hidden ARM64 test VM.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
out="$root/output/r05/winvm"
mkdir -p "$out"

bounded() {
    /usr/bin/perl -e 'alarm shift; exec @ARGV' "$@"
}

guest() {
    bounded "$(($1 + 60))" "/Users/stix/Library/Containers/com.utmapp.UTM/Data/Documents/win11-assets/tools/gx.sh" "$@"
}

utm() {
    limit=$1
    shift
    bounded "$limit" /Applications/UTM.app/Contents/MacOS/utmctl "$@"
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
scratch=$(mktemp -d "$out/vm.XXXXXXXX")
vmroot="C:\\$(basename "$scratch")"
started=0
reachable=0
prepared=0

cleanup() {
    result=$?
    trap - EXIT HUP INT TERM
    for file in "$scratch/source.zip" "$scratch/build.ps1" "$scratch/receipt"; do
        if [ -f "$file" ]; then
            rm "$file" || result=1
        fi
    done
    if [ "$reachable" -eq 1 ]; then
        if [ "$prepared" -eq 1 ]; then
            guest 20 "Remove-Item -LiteralPath '$vmroot' -Recurse -ErrorAction Stop" > "$scratch/guest-cleanup.log" 2>&1 || :
        fi
        guest 15 'shutdown.exe /s /t 0; exit $LASTEXITCODE' > "$scratch/shutdown.log" 2>&1 || :
        stopped=0
        for attempt in 1 2 3; do
            if utm 7 status --hide cod2-win11 >> "$scratch/shutdown.log" 2>&1 &&
                tail -n 1 "$scratch/shutdown.log" | grep -iq '^[[:space:]]*stopped[[:space:]]*$'; then
                stopped=1
                break
            fi
            sleep 4
        done
        if [ "$stopped" -ne 1 ]; then
            echo "UTM did not confirm shutdown. See $scratch/shutdown.log." >&2
            exit 1
        fi
    fi
    if [ "$started" -eq 1 ]; then
        bounded 10 /usr/bin/osascript -e 'tell application "UTM" to quit' >> "$scratch/shutdown.log" 2>&1 || result=1
    fi
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
param([ValidateSet('core', 'shell')][string]$Phase)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$work = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $work 'cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
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
        Expand-Archive -LiteralPath $sdkZip -DestinationPath (Join-Path $work 'sdk')
        Remove-Item -LiteralPath $sdkZip
        Expand-Archive -LiteralPath (Join-Path $work 'source.zip') -DestinationPath (Join-Path $work 'src')
        Remove-Item -LiteralPath (Join-Path $work 'source.zip')
        Set-Location (Join-Path $work 'src\dotnet')
        Invoke-DotNet --info
        Invoke-DotNet restore CoD2Unbound.sln --locked-mode --source $source -p:NuGetAudit=false -m:4
        Invoke-DotNet build CoD2Unbound.sln --no-restore -c Release -m:4
        Invoke-DotNet run --project CoD2Unbound.Core.Tests -c Release --no-build
    } else {
        Set-Location (Join-Path $work 'src\dotnet')
        $project = 'CoD2Unbound.Windows\CoD2Unbound.Windows.csproj'
        foreach ($platform in 'ARM64', 'x64') {
            $rid = "win-$($platform.ToLowerInvariant())"
            $output = Join-Path $work $rid
            Invoke-DotNet restore $project -r $rid "-p:Platform=$platform" --source $source -p:NuGetAudit=false -m:4
            Invoke-DotNet publish $project --no-restore -c Release -r $rid "-p:Platform=$platform" -o $output -m:4
            $exe = Join-Path $output 'CoD2Unbound.Windows.exe'
            if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing $rid launcher executable" }
            $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
            Write-Output "$rid publish contains $($files.Count) files and $(($files | Measure-Object Length -Sum).Sum) bytes."
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

started=1
utm 45 start --hide cod2-win11 > "$scratch/start.log" 2>&1
# The guest agent may register after the VM starts. A successful transfer gives
# the host a receipt without assuming gx.sh propagates the guest's exit status.
for attempt in 1 2 3; do
    guest 25 "\$ErrorActionPreference = 'Stop'; if (!(Test-Path -LiteralPath '$vmroot')) { New-Item -Path '$vmroot' -ItemType Directory | Out-Null }; Set-Content -LiteralPath '$vmroot\ready' -Value ready -Encoding ascii" >> "$scratch/guest-ready.log" 2>&1 || :
    if utm 12 file pull --hide cod2-win11 "$vmroot\ready" > "$scratch/receipt" &&
        [ "$(tr -d '\r\n' < "$scratch/receipt")" = ready ]; then
        reachable=1
        prepared=1
        break
    fi
    sleep 8
done
[ "$reachable" -eq 1 ] || exit 1

utm 210 file push --hide cod2-win11 "$vmroot\source.zip" < "$scratch/source.zip"
utm 20 file push --hide cod2-win11 "$vmroot\build.ps1" < "$scratch/build.ps1"

run_phase() {
    phase=$1
    seconds=$2
    check_disk
    guest "$seconds" "& '$vmroot\build.ps1' -Phase '$phase'; exit \$LASTEXITCODE" > "$scratch/$phase.log" 2>&1 || :
    utm 20 file pull --hide cod2-win11 "$vmroot\downloads.csv" > "$scratch/downloads.csv" || :
    utm 12 file pull --hide cod2-win11 "$vmroot\\$phase.ok" > "$scratch/receipt" &&
        [ "$(tr -d '\r\n' < "$scratch/receipt")" = "$phase" ]
}

run_phase core 660
run_phase shell 900
