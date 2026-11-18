#!/usr/bin/env python3
"""Stub coordinator for tests/e2e.sh: serves three queued jobs and appends each call to <events> as a JSON line.
Usage: stub_server.py <portfile> <events>"""
import hashlib, http.server, json, sys

JOBS = [
    {"jobId": 42, "portName": "libfoo", "version": "1.0", "platform": "arm64", "recipePath": "dev-libs/libfoo/libfoo-1.0.recipe", "build": 1},
    {"jobId": 43, "portName": "infra", "version": "1.0", "platform": "arm64", "recipePath": "x/infra/infra-1.0.recipe"},
    {"jobId": 44, "portName": "sleeper", "version": "1.0", "platform": "arm64", "recipePath": "x/sleeper/sleeper-1.0.recipe"},
]
portfile, events = sys.argv[1], sys.argv[2]


def record(**event):
    with open(events, "a") as f:
        f.write(json.dumps(event) + "\n")


def parse_multipart(body, ctype):
    """The parts in order as (name, filename, data)."""
    boundary = ctype.split("boundary=", 1)[1].encode()
    parts = []
    for chunk in body.split(b"--" + boundary)[1:]:
        if chunk.startswith(b"--"):
            break
        head, data = chunk[2:].split(b"\r\n\r\n", 1)
        disp = head.split(b"\r\n")[0].decode()
        name = disp.split('name="')[1].split('"')[0]
        filename = disp.split('filename="')[1].split('"')[0] if "filename=" in disp else None
        parts.append((name, filename, data[:-2]))
    return parts


class Handler(http.server.BaseHTTPRequestHandler):
    def reply(self, status, obj=None):
        data = json.dumps(obj).encode() if obj is not None else b""
        self.send_response(status)
        self.send_header("Content-Length", str(len(data)))
        if data:
            self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(data)

    def authorized(self):
        if self.headers.get("Authorization") != "Bearer tok":
            self.reply(401)
            return False
        return True

    def do_GET(self):
        if not self.authorized():
            return
        if self.path == "/api/client/me":
            self.reply(200, {"name": "e2e", "platform": "arm64", "owner": "tester",
                             "portsRepo": {"url": "https://example.invalid/ports.git", "branch": "main"}})
        elif self.path.startswith("/api/client/jobs?"):
            self.reply(200, {"total": len(JOBS)})
        else:
            self.reply(404)

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        if self.path == "/api/client/token":
            self.reply(200, {"accessToken": "tok"})
            return
        if not self.authorized():
            return
        parts = self.path.strip("/").split("/")
        if self.path == "/api/client/jobs/next":
            if JOBS:
                job = JOBS.pop(0)
                record(call="claim", job=job["jobId"])
                self.reply(200, job)
            else:
                self.reply(204)
        elif parts[-1] == "packages":
            fields = parse_multipart(body, self.headers["Content-Type"])
            names = [p[0] for p in fields]
            data = fields[-1][2]
            ok = names == ["sha256", "buildDate", "file"] and fields[0][2].decode() == hashlib.sha256(data).hexdigest()
            record(call="upload", job=int(parts[3]), names=names, file=fields[-1][1], sha_ok=ok, date=fields[1][2].decode(), size=len(data))
            self.reply(200 if ok else 400, {"fileName": fields[-1][1]})
        elif parts[-1] in ("complete", "fail", "release"):
            obj = json.loads(body)
            record(call=parts[-1], job=int(parts[3]), summary=obj.get("summary") or obj.get("reason"), log_len=len(obj.get("log", "")),
                   log_has_build=("mock build" in obj.get("log", "")))
            if parts[-1] == "complete":
                self.reply(200, {"status": "Verifying"})
            else:
                self.reply(204)
        else:
            self.reply(404)

    def log_message(self, *args):
        pass


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
with open(portfile, "w") as f:
    f.write(str(server.server_address[1]))
server.serve_forever()
