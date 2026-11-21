#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
image=mcr.microsoft.com/dotnet/sdk@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4
exec docker run --rm --network none --cpus 4 \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_PROCESSOR_COUNT=4 \
    --mount "type=bind,source=$root,target=/src,readonly" \
    -w /src/dotnet "$image" sh -eu -c '
        dotnet --version
        dotnet build CoD2Unbound.sln --artifacts-path /tmp/artifacts -c Release -m:4
        dotnet /tmp/artifacts/bin/CoD2Unbound.Core.Tests/release/CoD2Unbound.Core.Tests.dll
    '
