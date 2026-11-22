"""Create a Windows catalog that binds files by relative name and raw SHA-256."""

import hashlib
import subprocess

from release import sha256


def make_catalog(paths, root, catalog, work):
    members = [(path.relative_to(root).as_posix(), sha256(path)) for path in paths]
    if not members:
        raise ValueError("Cannot write a catalog with no members")
    identifier = hashlib.sha256(repr(members).encode()).hexdigest()[:32]
    entries = "".join(f"file{i} = SEQUENCE:file{i}\n" for i in range(len(members)))
    # Authenticode catalogs encode a CTL in PKCS #7 SignedData. Use a v2 member
    # identifier, generic file SIP, and a SHA-256 SpcIndirectDataContent.
    config = work / f"{catalog.stem}-catalog.cnf"
    header = f"""asn1 = SEQUENCE:pkcs7
[pkcs7]
type = OID:1.2.840.113549.1.7.2
content = EXPLICIT:0,SEQUENCE:signed
[signed]
version = INTEGER:1
digestAlgorithms = SET:empty
contentInfo = SEQUENCE:content
signerInfos = SET:empty
[empty]
[content]
type = OID:1.3.6.1.4.1.311.10.1
ctl = EXPLICIT:0,SEQUENCE:ctl
[ctl]
usage = SEQUENCE:usage
identifier = FORMAT:HEX,OCTETSTRING:{identifier}
thisUpdate = UTCTIME:260101000000Z
subjectAlgorithm = SEQUENCE:catalogVersion
members = SEQUENCE:members
[usage]
catalogList = OID:1.3.6.1.4.1.311.12.1.1
[catalogVersion]
catalogList = OID:1.3.6.1.4.1.311.12.1.3
parameters = NULL
[members]
{entries}
[memberInfo]
type = OID:1.3.6.1.4.1.311.12.2.3
values = SET:memberValues
[memberValues]
generic = IMPLICIT:2,SEQUENCE:empty
[genericFile]
type = OID:1.3.6.1.4.1.311.2.1.25
value = IMPLICIT:2,SEQUENCE:emptyFile
[emptyFile]
filename = IMPLICIT:0,OCTETSTRING:
[sha256]
type = OID:2.16.840.1.101.3.4.2.1
parameters = NULL
"""
    config.write_text(header + "".join(member(i, *item) for i, item in enumerate(members)))
    subprocess.run(["openssl", "asn1parse", "-genconf", config, "-noout", "-out", catalog],
                   check=True)


def member(i, name, digest):
    filename = (name + chr(0)).encode("utf-16le").hex()
    return f"""
[file{i}]
identifier = FORMAT:HEX,OCTETSTRING:{digest}
attributes = SET:attributes{i}
[attributes{i}]
memberInfo = SEQUENCE:memberInfo
name = SEQUENCE:nameAttribute{i}
digest = SEQUENCE:indirectAttribute{i}
[nameAttribute{i}]
type = OID:1.3.6.1.4.1.311.12.2.1
values = SET:nameValues{i}
[nameValues{i}]
encoded = SEQUENCE:name{i}
[name{i}]
key = BMPSTRING:File
flags = INTEGER:0x10010001
filename = FORMAT:HEX,OCTETSTRING:{filename}
[indirectAttribute{i}]
type = OID:1.3.6.1.4.1.311.2.1.4
values = SET:indirectValues{i}
[indirectValues{i}]
file = SEQUENCE:indirect{i}
[indirect{i}]
data = SEQUENCE:genericFile
messageDigest = SEQUENCE:digest{i}
[digest{i}]
algorithm = SEQUENCE:sha256
value = FORMAT:HEX,OCTETSTRING:{digest}
"""
