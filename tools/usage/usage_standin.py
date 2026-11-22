#!/usr/bin/env python3
"""Loopback receiver for the launcher's PostHog batches.

Start with --port 0 --record requests.jsonl. The printed URL is COD2_USAGE_URL.
Use --reply fail,ok to fail the first batch, or stall to delay each response.
POST /standin {"reply":"ok"} replaces the script. Only /batch/ is recorded.
Records are the original JSON bodies, one per line, including failed batches.
"""
import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def script(value):
    steps = value.split(",")
    if not steps or any(step not in {"ok", "fail", "stall", "drop"} for step in steps):
        raise ValueError("replies must be ok, fail, stall or drop")
    return steps


class Handler(BaseHTTPRequestHandler):
    def reply(self, status):
        body = b'{"status":1}' if status == 200 else b'{}'
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 1_048_576:
                raise ValueError("invalid length")
            body = self.rfile.read(length)
            data = json.loads(body)
            if not isinstance(data, dict):
                raise ValueError("expected a JSON object")
            if self.path == "/standin":
                steps = script(data["reply"])
                with self.server.lock:
                    self.server.steps = steps
                return self.reply(200)
            if self.path != "/batch/":
                return self.reply(404)
            if not data.get("api_key") or not isinstance(data.get("batch"), list) or not data["batch"]:
                raise ValueError("expected a batch")
        except (KeyError, TypeError, ValueError):
            return self.reply(400)
        with self.server.lock:
            with open(self.server.record, "ab") as journal:
                journal.write(body + b"\n")
            steps = self.server.steps
            step = steps.pop(0) if len(steps) > 1 else steps[0]
        if step == "stall":
            time.sleep(self.server.delay)
        if step != "drop":
            self.reply(503 if step == "fail" else 200)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--record", required=True)
    parser.add_argument("--reply", default="ok")
    parser.add_argument("--delay", type=float, default=10)
    args = parser.parse_args()
    try:
        steps = script(args.reply)
    except ValueError as error:
        parser.error(str(error))
    with ThreadingHTTPServer(("127.0.0.1", args.port), Handler) as server:
        server.lock = threading.Lock()
        server.steps, server.delay, server.record = steps, args.delay, args.record
        print(f"usage stand-in listening on http://127.0.0.1:{server.server_port}", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
