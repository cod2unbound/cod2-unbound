"""Create an unsigned, single-member Windows catalog for the raw ZIP SHA-256."""

import subprocess

from release import sha256


def make_catalog(archive, catalog, work):
    digest = sha256(archive)
    # Authenticode catalogs encode a CTL in PKCS #7 SignedData. Use a v2 member
    # identifier, generic file SIP, and a SHA-256 SpcIndirectDataContent.
    config = work / "catalog.cnf"
    config.write_text(f"""asn1 = SEQUENCE:pkcs7
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
identifier = FORMAT:HEX,OCTETSTRING:{digest[:32]}
thisUpdate = UTCTIME:260101000000Z
subjectAlgorithm = SEQUENCE:catalogVersion
members = SEQUENCE:members
[usage]
catalogList = OID:1.3.6.1.4.1.311.12.1.1
[catalogVersion]
catalogList = OID:1.3.6.1.4.1.311.12.1.3
parameters = NULL
[members]
zip = SEQUENCE:zip
[zip]
identifier = FORMAT:HEX,OCTETSTRING:{digest}
attributes = SET:attributes
[attributes]
memberInfo = SEQUENCE:memberInfo
name = SEQUENCE:nameAttribute
digest = SEQUENCE:indirectAttribute
[memberInfo]
type = OID:1.3.6.1.4.1.311.12.2.3
values = SET:memberValues
[memberValues]
generic = IMPLICIT:2,SEQUENCE:empty
[nameAttribute]
type = OID:1.3.6.1.4.1.311.12.2.1
values = SET:nameValues
[nameValues]
encoded = SEQUENCE:name
[name]
key = BMPSTRING:File
flags = INTEGER:0x10010001
filename = FORMAT:HEX,OCTETSTRING:{(archive.name + chr(0)).encode("utf-16le").hex()}
[indirectAttribute]
type = OID:1.3.6.1.4.1.311.2.1.4
values = SET:indirectValues
[indirectValues]
file = SEQUENCE:indirect
[indirect]
data = SEQUENCE:genericFile
messageDigest = SEQUENCE:digest
[genericFile]
type = OID:1.3.6.1.4.1.311.2.1.25
value = IMPLICIT:2,SEQUENCE:emptyFile
[emptyFile]
filename = IMPLICIT:0,OCTETSTRING:
[digest]
algorithm = SEQUENCE:sha256
value = FORMAT:HEX,OCTETSTRING:{digest}
[sha256]
type = OID:2.16.840.1.101.3.4.2.1
parameters = NULL
""")
    subprocess.run(["openssl", "asn1parse", "-genconf", config, "-noout", "-out", catalog],
                   check=True)
