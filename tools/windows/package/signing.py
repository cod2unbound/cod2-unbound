"""Sign the release with Jsign, then verify it independently with osslsigncode."""

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
from urllib.parse import urlsplit

from release import sha256


def openssl(command, work):
    return subprocess.run(["openssl", *command], cwd=work, check=True,
                          capture_output=True).stdout


@contextmanager
def test_authority(work):
    # Separate disposable code-signing and RFC 3161 roots. No public TSA or cloud account.
    with tempfile.TemporaryDirectory(prefix="keys-", dir=work) as location:
        p = Path(location)
        for name, eku in (("code", "codeSigning"), ("tsa", "timeStamping")):
            openssl(["req", "-new", "-x509", "-newkey", "rsa:3072", "-nodes", "-days", "7",
                     "-subj", f"/CN=CoD2 Unbound Package Test {name}",
                     "-addext", "basicConstraints=critical,CA:FALSE",
                     "-addext", "keyUsage=critical,digitalSignature",
                     "-addext", f"extendedKeyUsage=critical,{eku}",
                     "-keyout", f"{name}.key", "-out", f"{name}.pem"], p)
        (p / "tsa.serial").write_text("01\n")
        (p / "tsa.cnf").write_text(f"""[tsa]
default_tsa = local
[local]
serial = {p}/tsa.serial
signer_cert = {p}/tsa.pem
signer_key = {p}/tsa.key
signer_digest = sha256
default_policy = 1.3.6.1.4.1.0
digests = sha256
""")
        for name in ("code", "tsa"):
            (work / f"test-{name}.pem").write_bytes((p / f"{name}.pem").read_bytes())

        class Timestamp(BaseHTTPRequestHandler):
            def log_message(self, format, *args):
                pass

            def do_POST(self):
                (p / "request.tsq").write_bytes(self.rfile.read(int(self.headers["Content-Length"])))
                data = openssl(["ts", "-reply", "-config", "tsa.cnf", "-queryfile", "request.tsq"], p)
                self.send_response(200)
                self.send_header("Content-Type", "application/timestamp-reply")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        with HTTPServer(("127.0.0.1", 0), Timestamp) as http:
            worker = threading.Thread(target=http.serve_forever)
            worker.start()
            try:
                yield [
                    "--keyfile", p / "code.key", "--certfile", p / "code.pem",
                    "--tsaurl", f"http://127.0.0.1:{http.server_port}", "--tsmode", "RFC3161",
                ], work / "test-code.pem", work / "test-tsa.pem"
            finally:
                http.shutdown()
                worker.join()


def azure_options(work, trust_root):
    for name in ("ENDPOINT", "ACCOUNT", "PROFILE", "TOKEN"):
        if not os.environ.get(f"COD2_SIGN_{name}"):
            raise ValueError(f"Set COD2_SIGN_{name} for Azure signing")
    endpoint = os.environ["COD2_SIGN_ENDPOINT"].removesuffix("/")
    url = urlsplit(endpoint)
    if (url.scheme != "https" or not url.hostname or url.netloc != url.hostname
            or url.path or url.query or url.fragment):
        raise ValueError("COD2_SIGN_ENDPOINT must be the regional HTTPS service origin")
    account = os.environ["COD2_SIGN_ACCOUNT"]
    profile = os.environ["COD2_SIGN_PROFILE"]
    if any(not re.fullmatch(r"[A-Za-z0-9-]+", value) for value in (account, profile)):
        raise ValueError("Signing account and profile must be Azure resource names")
    root = work / "azure-trust.pem"
    root.write_bytes(openssl(["x509", "-inform", "DER", "-in", trust_root], work))
    return [
        "--storetype", "TRUSTEDSIGNING", "--keystore", endpoint,
        "--storepass", "env:COD2_SIGN_TOKEN", "--alias", f"{account}/{profile}",
    ], root, root


class Signer:
    def __init__(self, jar, options, code_root, tsa_root, report):
        self.jar = jar
        self.options = options
        self.code_root = code_root
        self.tsa_root = tsa_root
        self.report = report

    def sign(self, paths):
        subprocess.run(["java", "-jar", self.jar, "sign", "--alg", "SHA-256",
                        "--replace", "--name", "CoD2 Unbound", *self.options, *paths],
                       check=True, timeout=600)
        for path in paths:
            self.verify(path)

    def verify(self, path, archive=None):
        result = subprocess.run(
            ["osslsigncode", "verify", "-verbose", "-CAfile", self.code_root,
             "-TSA-CAfile", self.tsa_root, "-in", path],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=120,
        )
        with self.report.open("a") as log:
            log.write(f"\n{path}\n{result.stdout}")
        result.check_returncode()
        if not re.search(r"(?mi)^Message digest algorithm\s*:\s*SHA256\s*$", result.stdout):
            raise ValueError(f"{path}: expected a SHA-256 signature, see {self.report}")
        if (not re.search(r"(?m)^Timestamp Server Signature verification: ok\s*$", result.stdout)
                or "Timestamp verified using:" not in result.stdout):
            raise ValueError(f"{path}: missing verified timestamp, see {self.report}")
        if archive and not re.search(
            rf"(?mi)^\s*Message digest\s*:\s*{sha256(archive)}\s*$", result.stdout
        ):
            raise ValueError(f"{path}: the catalog does not bind {archive.name}")
