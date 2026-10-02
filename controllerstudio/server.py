"""Background service: runs the engine and serves the UI + JSON API on localhost."""
import json
import os
import subprocess
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import engine as eng
from .presets import PresetStore

WEB = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "web")
PORT = int(os.environ.get("CSP_PORT", "8765"))
TYPES = {".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".svg": "image/svg+xml"}

store = PresetStore()
engine = eng.Engine()


MOONLIGHT = "com.moonlight_stream.Moonlight"
# Keep Moonlight's SDL away from the real controller so it only sees the virtual one.
MOONLIGHT_ENV = {
    "SDL_GAMECONTROLLER_IGNORE_DEVICES": "0x054c/0x0ce6,0x054c/0x0df2",
    "SDL_JOYSTICK_HIDAPI_PS5": "0",
}


def local_ips():
    """This machine's addresses, for the game data setup instructions."""
    try:
        out = subprocess.run(["ip", "-4", "-o", "addr", "show", "scope", "global"],
                             capture_output=True, text=True, timeout=3).stdout
    except (OSError, subprocess.TimeoutExpired):
        return []
    return [line.split()[3].split("/")[0] for line in out.splitlines()]


def moonlight_hidden():
    """True when Moonlight is set to ignore the real controller."""
    try:
        out = subprocess.run(["flatpak", "override", "--user", "--show", MOONLIGHT],
                             capture_output=True, text=True, timeout=5).stdout
    except (OSError, subprocess.TimeoutExpired):
        return None
    return "SDL_GAMECONTROLLER_IGNORE_DEVICES" in out


def set_moonlight_hidden(hide):
    if hide:
        args = [f"--env={k}={v}" for k, v in MOONLIGHT_ENV.items()]
    else:
        args = [f"--unset-env={k}" for k in MOONLIGHT_ENV]
    subprocess.run(["flatpak", "override", "--user", *args, MOONLIGHT],
                   capture_output=True, timeout=10, check=False)


def test_effect(body):
    """Effects the UI can play on demand (Feel It buttons and the Trigger Lab)."""
    kind = body.get("kind")
    side = body.get("trigger", "r2")
    seconds = body.get("seconds", 1.5)
    if kind == "feel":
        fx = eng.feel_effect(body["feel"])
    elif kind == "reaction":
        e = body["effect"]
        fx = eng.reaction_effect(e)
        seconds = max(seconds, e.get("duration_ms", 60) / 1000) if body.get("hold") else e.get("duration_ms", 60) / 1000
    elif kind == "stop":
        engine.set_override(None, None, 0)
        return
    else:
        raise ValueError(kind)
    if side == "l2":
        engine.set_override(fx, eng.OFF, seconds)
    elif side == "both":
        engine.set_override(fx, fx, seconds)
    else:
        engine.set_override(eng.OFF, fx, seconds)


class Handler(BaseHTTPRequestHandler):
    server_version = "ControllerStudioPro/1.0"

    def log_message(self, *args):
        pass

    def _json(self, obj, code=200):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def _status(self):
        return dict(engine.snapshot(), active=store.active_id, presets=store.all(),
                    settings=store.settings, moonlight_hidden=moonlight_hidden(),
                    app_dir=os.path.dirname(WEB), ips=local_ips(), hidden_builtins=store.hidden_count)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/api/status":
            return self._json(self._status())
        if path == "/api/stream":
            return self._stream()
        if path == "/":
            path = "/index.html"
        f = os.path.normpath(os.path.join(WEB, path.lstrip("/")))
        if not f.startswith(WEB) or not os.path.isfile(f):
            return self._json({"error": "not found"}, 404)
        data = open(f, "rb").read()
        self.send_response(200)
        self.send_header("Content-Type", TYPES.get(os.path.splitext(f)[1], "application/octet-stream"))
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _stream(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        try:
            while True:
                snap = engine.snapshot()
                snap["active"] = store.active_id
                self.wfile.write(b"data: " + json.dumps(snap).encode() + b"\n\n")
                self.wfile.flush()
                time.sleep(1 / 60)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_POST(self):
        try:
            body = self._body()
            path = self.path
            if path == "/api/active":
                store.set_active(body["id"])
                engine.set_preset(store.active())
            elif path == "/api/enabled":
                engine.set_enabled(body["enabled"])
            elif path == "/api/test":
                test_effect(body)
            elif path == "/api/rumble":
                engine.rumble(body.get("strong", 0.8), body.get("weak", 0.8), body.get("ms", 600))
            elif path == "/api/settings":
                if body.get("output", {}).get("virtual") and not eng.uinput_available():
                    raise ValueError("Set up device access first (see Output)")
                before = store.settings["output"]["virtual"]
                settings = store.update_settings(body)
                engine.set_settings(settings)
                if settings["output"]["virtual"] != before or body.get("fix_moonlight"):
                    set_moonlight_hidden(settings["output"]["virtual"])
            elif path == "/api/presets/restore":
                store.restore_builtins()
            elif m := re.fullmatch(r"/api/presets/([\w-]+)/duplicate", path):
                return self._json(store.duplicate(m[1]))
            elif m := re.fullmatch(r"/api/presets/([\w-]+)/reset", path):
                store.reset(m[1])
                if m[1] == store.active_id:
                    engine.set_preset(store.active())
            else:
                return self._json({"error": "not found"}, 404)
            return self._json(self._status())
        except (KeyError, ValueError) as e:
            return self._json({"error": str(e)}, 400)

    def do_PUT(self):
        m = re.fullmatch(r"/api/presets/([\w-]+)", self.path)
        if not m:
            return self._json({"error": "not found"}, 404)
        try:
            store.update(m[1], self._body())
        except KeyError as e:
            return self._json({"error": str(e)}, 404)
        if m[1] == store.active_id:
            engine.set_preset(store.active())
        return self._json(self._status())

    def do_DELETE(self):
        m = re.fullmatch(r"/api/presets/([\w-]+)", self.path)
        if not m:
            return self._json({"error": "not found"}, 404)
        try:
            store.delete(m[1])
        except ValueError as e:
            return self._json({"error": str(e)}, 400)
        engine.set_preset(store.active())
        return self._json(self._status())


def main():
    engine.set_preset(store.active())
    engine.set_settings(store.settings)
    engine.start()
    httpd = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    httpd.daemon_threads = True
    print(f"Controller Studio Pro service on http://127.0.0.1:{PORT}", file=sys.stderr)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        dev = engine.dev
        if dev:
            try:
                dev.send(eng.OFF, eng.OFF)
            except OSError:
                pass


if __name__ == "__main__":
    main()
