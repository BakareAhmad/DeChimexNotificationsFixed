#!/usr/bin/env python3
"""Sanitized local Brevo-compatible stub for the marketing-builder browser E2E.

Never stores or prints request headers. The only key-related fields retained are booleans.
"""
from __future__ import annotations

import hmac
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

HOST = os.environ.get("STUB_HOST", "127.0.0.1")
PORT = int(os.environ.get("STUB_PORT", "9998"))
EXPECTED_KEY = os.environ.get("STUB_API_KEY", "marketing-builder-e2e-dummy")
CAPTURE_PATH = Path(os.environ.get("STUB_CAPTURE_PATH", "/tmp/marketing-builder-captured.jsonl"))
CAPTURE_PATH.parent.mkdir(parents=True, exist_ok=True)
LOCK = threading.Lock()
CAPTURED: list[dict[str, Any]] = []


class Handler(BaseHTTPRequestHandler):
    server_version = "LocalEmailStub/1.0"

    def _reply(self, status: int, body: Any) -> None:
        raw = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._reply(200, {"status": "ok"})
            return
        if self.path == "/control/captured":
            with LOCK:
                snapshot = list(CAPTURED)
            self._reply(200, snapshot)
            return
        self._reply(404, {"error": "not found"})

    def _read_request_body(self) -> bytes:
        transfer_encoding = self.headers.get("Transfer-Encoding", "").lower()
        if "chunked" in transfer_encoding:
            chunks: list[bytes] = []
            while True:
                line = self.rfile.readline().strip()
                if not line:
                    continue
                size = int(line.split(b";", 1)[0], 16)
                if size == 0:
                    while self.rfile.readline().strip():
                        pass  # trailer headers, if any
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)  # trailing CRLF
            return b"".join(chunks)
        size = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(size)

    def do_POST(self) -> None:  # noqa: N802
        if self.path != "/smtp/email":
            self._reply(404, {"error": "not found"})
            return
        try:
            payload = json.loads(self._read_request_body())
        except (ValueError, json.JSONDecodeError):
            self._reply(400, {"error": "invalid JSON"})
            return

        key = self.headers.get("api-key", "")
        # Persist only the message fields needed for E2E assertions; never retain request headers.
        recipients = payload.get("to") or []
        message = {
            "sender": payload.get("sender"),
            "to": recipients,
            "subject": payload.get("subject"),
            "htmlContent": payload.get("htmlContent", ""),
            "textContent": payload.get("textContent", ""),
            "replyTo": payload.get("replyTo"),
            "providerKeyPresent": bool(key),
            "providerKeyMatchesExpected": bool(EXPECTED_KEY) and hmac.compare_digest(key, EXPECTED_KEY),
        }
        with LOCK:
            CAPTURED.append(message)
            with CAPTURE_PATH.open("a", encoding="utf-8") as capture:
                capture.write(json.dumps(message, ensure_ascii=False) + "\n")
        print(json.dumps({"event": "email-accepted", "recipientCount": len(recipients), "status": 201}), flush=True)
        self._reply(201, {"messageId": f"local-{len(CAPTURED)}"})

    def log_message(self, _format: str, *_args: Any) -> None:
        # Suppress the default request/header-bearing diagnostics entirely.
        return


if __name__ == "__main__":
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(json.dumps({"event": "stub-ready", "host": HOST, "port": PORT}), flush=True)
    server.serve_forever()
