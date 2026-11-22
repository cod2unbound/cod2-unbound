# Run on Windows against the signed distribution asset. Take rules from a file
# so the configured OIDs never appear on the command line or in this receipt.
$ErrorActionPreference = 'Stop'
$setupFiles = @()
$referenceFiles = @()
$rulesFile = $null
$minimumArcs = 9
$codeSigning = '1.3.6.1.5.5.7.3.3'

function Refuse([string]$reason) {
    [Console]::Error.WriteLine("Publisher check refused: $reason")
    exit 1
}

function Read-LeafUsages([Security.Cryptography.X509Certificates.X509Certificate2]$certificate) {
    $extension = $certificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }
    if ($null -eq $extension -or @($extension).Count -ne 1) { Refuse 'expected one leaf EKU extension' }
    $usages = @($extension.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($codeSigning -notin $usages) { Refuse 'the certificate is missing the code signing EKU' }
    return $usages
}

function Get-OidHash([string]$oid) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($oid))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

try {
    for ($i = 0; $i -lt $args.Count; $i += 2) {
        if ($i + 1 -ge $args.Count) { Refuse 'an option is missing its value' }
        $value = $args[$i + 1]
        switch -CaseSensitive ($args[$i]) {
            '--setup' { $setupFiles += $value }
            '--rules-file' {
                if ($rulesFile) { Refuse 'provide one rules file' }
                $rulesFile = $value
            }
            '--other-publisher' { $referenceFiles += $value }
            '--min-arcs' {
                if ($value -notmatch '^[0-9]{1,3}$' -or [int]$value -lt 4) { Refuse 'minimum arcs must be 4 through 999' }
                $minimumArcs = [int]$value
            }
            default { Refuse 'unknown option' }
        }
    }
    if (!$setupFiles -or !$rulesFile) {
        Refuse 'use --setup FILE --rules-file FILE [--other-publisher CERTIFICATE] [--min-arcs COUNT]'
    }

    $oids = @()
    $number = 0
    foreach ($entry in ((Get-Content -LiteralPath $rulesFile -Raw).Trim() -split '\|')) {
        $number++
        if ($entry -cnotmatch '^([0-9a-fA-F]{64})=((?:1\.3\.6\.1\.4\.1|2\.25)(?:\.(?:0|[1-9][0-9]*)){2,})$') {
            Refuse "invalid format for configured rule $number"
        }
        $oid = $Matches[2]
        $arcCount = $oid.Split('.').Length
        "Rule $number OID SHA-256 $(Get-OidHash $oid) has $arcCount arcs (minimum $minimumArcs)"
        if ($arcCount -lt $minimumArcs) { Refuse "rule $number has too few arcs" }
        $oids += $oid
    }
    $oids = @($oids | Select-Object -Unique)
    $observedOids = @()
    $leafThumbprints = @()
    $number = 0
    foreach ($file in $setupFiles) {
        $number++
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or !$signature.SignerCertificate) {
            Refuse "Setup $number needs a valid embedded Authenticode signature"
        }
        $leafThumbprints += $signature.SignerCertificate.Thumbprint
        $usages = Read-LeafUsages $signature.SignerCertificate
        $matchesForSetup = @($oids | Where-Object { $_ -in $usages })
        if (!$matchesForSetup) { Refuse "Setup $number leaf contains none of the configured OIDs" }
        foreach ($oid in $matchesForSetup) {
            "Setup $number embedded signer leaf contains OID SHA-256 $(Get-OidHash $oid)"
            $observedOids += $oid
        }
    }
    foreach ($oid in $oids) {
        if ($oid -notin $observedOids) { Refuse "no Setup signer leaf contains OID SHA-256 $(Get-OidHash $oid)" }
    }

    $number = 0
    foreach ($file in $referenceFiles) {
        $number++
        $path = (Resolve-Path -LiteralPath $file).ProviderPath
        $reference = [Security.Cryptography.X509Certificates.X509Certificate2]::new($path)
        try {
            if ($reference.Thumbprint -in $leafThumbprints) { Refuse "reference $number is a Setup signer certificate" }
            $usages = Read-LeafUsages $reference
            foreach ($oid in $oids) {
                $hash = Get-OidHash $oid
                if ($oid -in $usages) { Refuse "reference $number also contains OID SHA-256 $hash" }
                "Reference $number does not contain OID SHA-256 $hash"
            }
        } finally { $reference.Dispose() }
    }
    if (!$referenceFiles) { 'No different-publisher reference was supplied.' }
    'Publisher check passed.'
} catch {
    # Certificate decoding and argument errors can echo input. Keep the receipt redacted.
    Refuse 'could not read or verify the supplied files'
}
