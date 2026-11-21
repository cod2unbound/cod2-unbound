#!/bin/sh
set -eu
if [ "$#" -ne 1 ]; then
    echo "Usage: sh dotnet/fetch-packages.sh FEED_DIRECTORY" >&2
    exit 2
fi
mkdir -p "$1"
feed=$(CDPATH= cd -- "$1" && pwd)
image=mcr.microsoft.com/dotnet/sdk@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4
exec docker run --rm --cpus 4 \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    --mount "type=bind,source=$feed,target=/feed" "$image" sh -eu -c '
        package=bouncycastle.cryptography.2.7.0.nupkg
        digest=f091ffccab4d03993e660bace277659a79dee0972f54d7f1f4bd46d680966241
        scratch=$(mktemp /feed/.package.XXXXXX)
        trap "rm -f \"\$scratch\"" EXIT
        curl -fsSL --max-time 60 "https://api.nuget.org/v3-flatcontainer/bouncycastle.cryptography/2.7.0/$package" -o "$scratch"
        printf "%s  %s\n" "$digest" "$scratch" | sha256sum -c -
        cp "$scratch" "/tmp/$package"
        dotnet nuget verify --all "/tmp/$package"
        chmod 644 "$scratch"
        mv -f "$scratch" "/feed/$package"
    '
