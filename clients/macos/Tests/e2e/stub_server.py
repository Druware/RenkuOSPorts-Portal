#!/usr/bin/env python3
"""Stub of the coordinator's client API for the end-to-end test.

Usage: stub_server.py <port-file> <events-file>
Hands out one job (42, arm64), checks each upload's multipart layout and SHA-256, and appends one JSON line per
request to <events-file>. Env: FIRST_401=1 answers the first api/client/me with 401 (token refresh path);
RELEASE_404=1 answers release with 404 (an older server).
"""
import hashlib
import json
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

port_file, events_file = sys.argv[1], sys.argv[2]
state = {"claimed": False, "me401": os.environ.get("FIRST_401") == "1"}


def record(**event):
    with open(events_file, "a") as f:
        f.write(json.dumps(event) + "\n")


def parse_multipart(body, content_type):
    """Returns [(name, bytes)] in body order, or raises ValueError."""
    m = re.search(r"boundary=(.+)$", content_type)
    if not m:
        raise ValueError("no boundary")
    sep = b"--" + m.group(1).encode()
    parts = body.split(sep)
    if parts[0] != b"" or not parts[-1].startswith(b"--"):
        raise ValueError("bad framing")
    out = []
    for part in parts[1:-1]:
        head, _, data = part.partition(b"\r\n\r\n")
        name = re.search(rb'name="([^"]+)"', head).group(1).decode()
        if not data.endswith(b"\r\n"):
            raise ValueError("part not CRLF-terminated")
        out.append((name, data[:-2]))
    return out


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, status, obj=None):
        body = b"" if obj is None else json.dumps(obj).encode()
        self.send_response(status)
        if obj is not None:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        return self.rfile.read(int(self.headers.get("Content-Length", 0)))

    def authorized(self):
        return self.headers.get("Authorization") == "Bearer tok-%d" % state.get("tokens", 0)

    def do_GET(self):
        path = self.path
        if not self.authorized():
            record(method="GET", path=path, status=401)
            return self.reply(401)
        if path == "/api/client/me":
            if state["me401"]:
                state["me401"] = False
                record(method="GET", path=path, status=401, forced=True)
                return self.reply(401)
            record(method="GET", path=path, status=200)
            return self.reply(200, {"id": "c1", "name": "mac-test", "platform": "arm64", "owner": "tester",
                                    "currentJob": None, "ownerClients": [],
                                    "portsRepo": {"url": "https://example.invalid/ports.git", "branch": "main"}})
        if path.startswith("/api/client/jobs?"):
            record(method="GET", path=path, status=200)
            return self.reply(200, {"total": 0 if state["claimed"] else 1, "items": []})
        self.reply(404)

    def do_POST(self):
        path, body = self.path, self.body()
        if path == "/api/client/token":
            req = json.loads(body)
            ok = req == {"clientId": "c1", "secret": "s3cret"}
            state["tokens"] = state.get("tokens", 0) + (1 if ok else 0)
            record(method="POST", path=path, status=200 if ok else 401)
            return self.reply(200, {"accessToken": "tok-%d" % state["tokens"]}) if ok else self.reply(401)
        if not self.authorized():
            record(method="POST", path=path, status=401)
            return self.reply(401)
        if path == "/api/client/jobs/next":
            if state["claimed"]:
                record(method="POST", path=path, status=204)
                return self.reply(204)
            state["claimed"] = True
            record(method="POST", path=path, status=200)
            return self.reply(200, {"jobId": 42, "portName": os.environ.get("PORT_NAME", "libfoo"), "version": "1.2",
                                    "category": "dev-libs", "recipePath": "dev-libs/libfoo/libfoo-1.2.recipe",
                                    "platform": "arm64", "build": 1})
        m = re.match(r"^/api/client/jobs/(\d+)/(packages|complete|fail|release)$", path)
        if not m:
            return self.reply(404)
        action = m.group(2)
        if action == "packages":
            try:
                parts = parse_multipart(body, self.headers.get("Content-Type", ""))
                names = [n for n, _ in parts]
                if names != ["sha256", "buildDate", "file"]:
                    raise ValueError("part order %s" % names)
                fields = dict(parts)
                if hashlib.sha256(fields["file"]).hexdigest() != fields["sha256"].decode():
                    raise ValueError("sha256 mismatch")
                if not re.match(rb"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", fields["buildDate"]):
                    raise ValueError("bad buildDate %r" % fields["buildDate"])
                filename = re.search(rb'filename="([^"]+)"', body).group(1).decode()
            except Exception as e:  # noqa: BLE001 - any parse problem is a 400 for the client
                record(method="POST", path=path, status=400, error=str(e))
                return self.reply(400, {"title": str(e)})
            record(method="POST", path=path, status=200, file=filename, size=len(fields["file"]))
            return self.reply(200, {"fileName": filename, "sha256": fields["sha256"].decode()})
        req = json.loads(body)
        if action == "release" and os.environ.get("RELEASE_404") == "1":
            record(method="POST", path=path, status=404, reason=req.get("reason"))
            return self.reply(404)
        record(method="POST", path=path, status=204 if action == "release" else 200,
               summary=req.get("summary"), reason=req.get("reason"), logBytes=len(req.get("log", "")))
        if action == "release":
            return self.reply(204)
        return self.reply(200, {"status": "Verifying" if action == "complete" else "Failed"})


server = HTTPServer(("127.0.0.1", 0), Handler)
with open(port_file, "w") as f:
    f.write(str(server.server_address[1]))
server.serve_forever()
