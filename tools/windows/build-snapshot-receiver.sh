#!/usr/bin/env bash
# Build the stock client's decoder and net channel without a renderer or sound.
set -euo pipefail
if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <output-directory>" >&2
    exit 2
fi
root=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$1"
output=$(cd "$1" && pwd)
exec docker run --rm --init --pull never --network none --platform linux/amd64 \
    --label cod2.windows.check=snapshot-receiver -v "$root:/src:ro" -v "$output:/out" \
    sha256:d23f4fa974304b285870e9785c2bddd7877e93f16a86c524cdcbffc0a061d208 bash -ec '
cd /src/src
gcc-14 -D_GNU_SOURCE -DCOD2_X64=1 -DCOD2_FEATURE_NET_HARDENING=1 -DCOD2_FEATURE_WWW_DOWNLOAD=0 \
    -DNOGL -I. -Iheaders -O2 -ffunction-sections -fdata-sections -Wno-incompatible-pointer-types \
    -static -Wl,--gc-sections -o /out/snapshot-receiver \
    /src/tools/windows/snapshot-receiver.c PC/qcommon/{msg_mp,huffman,net_chan_mp,net_hardening}.c \
    PC/client_mp/{cl_parse_mp,cl_net_chan_mp,cl_input}.c PC/universal/{com_shared,q_shared}.c -lm
'
