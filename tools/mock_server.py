"""Stand-in for upmy.stream in tests. Stdlib only.

    python3 tools/mock_server.py [--port 8787] [--key test]

Then start the client with:  upmystream-game-controller.exe --server http://<this-host>:8787
Type `user redeem [text]` + Enter to send a redeem, or `user : message` to send a chat message,
or POST /_send {"user": "...", "redeem": "...", "text": "..."} / {"type": "chat", "user": "...", "message": "w"}.
Chat messages only reach clients that asked for ?chat=1, like on the real server.
"""

import argparse
import json
import queue
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REDEEMS = [
    {"name": "fireball", "cost": 100, "enabled": True, "cooldown": None, "description": "Q"},
    {"name": "jump", "cost": 50, "enabled": True, "cooldown": None, "description": "Space"},
    {"name": "sprint", "cost": 50, "enabled": True, "cooldown": None, "description": "hold Shift"},
    {"name": "look", "cost": 80, "enabled": True, "cooldown": None, "description": "turn the camera"},
]
clients: list[tuple[queue.Queue, bool]] = []  # (queue, wants chat)
lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        if self.headers.get("X-API-Key") != args.key:
            return self._send(401, {"detail": "Invalid API Key"})
        path, _, query = self.path.partition("?")
        if path == "/api/v1/redeems":
            return self._send(200, REDEEMS)
        if path != "/api/v1/redeems/stream":
            return self._send(404, {"detail": "Not Found"})
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        q: queue.Queue = queue.Queue()
        entry = (q, "chat=1" in query.split("&"))
        with lock:
            clients.append(entry)
        print(f"client connected ({len(clients)})", flush=True)
        try:
            while True:
                try:
                    item = q.get(timeout=15)
                    self.wfile.write(f"data: {json.dumps(item)}\n\n".encode())
                except queue.Empty:
                    self.wfile.write(b": ping\n\n")
                self.wfile.flush()
        except OSError:
            pass
        finally:
            with lock:
                clients.remove(entry)
            print(f"client gone ({len(clients)})", flush=True)

    def do_POST(self):
        """Test hook: POST /_send {"user", "redeem", "text"} does the same as typing."""
        if self.path != "/_send":
            return self._send(404, {"detail": "Not Found"})
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        if body.get("type") == "chat":
            chat(body.get("user", "viewer"), body["message"])
        else:
            broadcast(body.get("user", "viewer"), body["redeem"], body.get("text", ""))
        self._send(200, {"clients": len(clients)})

    def _send(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


def broadcast(user: str, redeem: str, text: str) -> None:
    event = {"type": "redeem", "username": user, "reward_name": redeem, "user_input": text, "cost": 100}
    with lock:
        for q, _ in clients:
            q.put(event)
    print(f"sent {redeem} to {len(clients)} client(s)", flush=True)


def chat(user: str, message: str) -> None:
    event = {"type": "chat", "platform": "twitch", "username": user, "message": message}
    with lock:
        wanting = [q for q, wants in clients if wants]
        for q in wanting:
            q.put(event)
    print(f"chat '{message}' to {len(wanting)} client(s)", flush=True)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=8787)
    p.add_argument("--key", default="test")
    args = p.parse_args()
    server = ThreadingHTTPServer(("0.0.0.0", args.port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"mock upmy.stream on :{args.port}, key '{args.key}'. Type: user redeem [text]", flush=True)
    try:
        for line in iter(input, None):
            if " : " in line:
                user, message = line.split(" : ", 1)
                chat(user.strip(), message)
                continue
            parts = line.split(maxsplit=2)
            if len(parts) >= 2:
                broadcast(parts[0], parts[1], parts[2] if len(parts) > 2 else "")
    except EOFError:  # no terminal (started in the background): keep serving for /_send
        threading.Event().wait()
    except KeyboardInterrupt:
        pass
