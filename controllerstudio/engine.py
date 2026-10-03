"""Talks to the DualSense over hidraw: parses input, drives adaptive triggers,
lighting and rumble, runs each preset's reactions, and (optionally) feeds a
remapped virtual controller to games.

The engine runs in its own thread. Other threads call the public methods,
which only touch shared state under self.lock.
"""
import colorsys
import errno
import glob
import math
import os
import selectors
import socket
import struct
import sys
import threading
import time
import traceback
import zlib

from .telemetry import PORTS, GameTelemetry

WINDOWS = sys.platform == "win32"
if WINDOWS:
    from .winpad import VirtualPad, uinput_available  # noqa: F401 (re-exported)
else:
    import fcntl
    from .virtualpad import VirtualPad, uinput_available  # noqa: F401 (re-exported)

SONY = 0x054C
MODELS = {0x0CE6: "DualSense", 0x0DF2: "DualSense Edge"}

OFF = bytes([0x05] + [0] * 10)

# name -> (byte index into buttons[0..3], bit)
BUTTON_BITS = {
    "square": (0, 0x10), "cross": (0, 0x20), "circle": (0, 0x40), "triangle": (0, 0x80),
    "l1": (1, 0x01), "r1": (1, 0x02), "l2": (1, 0x04), "r2": (1, 0x08),
    "create": (1, 0x10), "options": (1, 0x20), "l3": (1, 0x40), "r3": (1, 0x80),
    "ps": (2, 0x01), "touchpad": (2, 0x02), "mute": (2, 0x04),
    "fn_left": (2, 0x10), "fn_right": (2, 0x20), "paddle_left": (2, 0x40), "paddle_right": (2, 0x80),
}
DPAD = {0: ("up",), 1: ("up", "right"), 2: ("right",), 3: ("down", "right"),
        4: ("down",), 5: ("down", "left"), 6: ("left",), 7: ("up", "left")}

INSTANT = {"buttons", "gear", "slam"}  # reactions that fire once for duration_ms
GAME_EVENTS = {"gear", "abs", "wheelspin", "redline"}


# ---------------------------------------------------------------- trigger effects

def _clamp(v, lo, hi):
    return max(lo, min(hi, int(round(v))))


def fx_zones(zones):
    """Resistance per zone: 10 zones from top to bottom, strength 0 (none)..8."""
    active = force = 0
    for i, s in enumerate(zones[:10]):
        s = _clamp(s, 0, 8)
        if s > 0:
            active |= 1 << i
            force |= (s - 1) << (3 * i)
    if not active:
        return OFF
    return bytes([0x21, active & 0xFF, active >> 8, *force.to_bytes(4, "little"), 0, 0, 0, 0])


def fx_smooth(start, force):
    """Continuous resistance from `start`% of the pull, `force`% strong (fine-grained)."""
    if force <= 0:
        return OFF
    return bytes([0x01, _clamp(start * 2.55, 0, 255), _clamp(force * 2.55, 0, 255)] + [0] * 8)


def fx_click(start, end, force):
    """A wall between zones start (2..7) and end (start+1..8) that breaks with a click."""
    start = _clamp(start, 2, 7)
    end = max(start + 1, _clamp(end, 3, 8))
    zones = (1 << start) | (1 << end)
    return bytes([0x25, zones & 0xFF, zones >> 8, _clamp(force, 1, 8) - 1] + [0] * 7)


def fx_vibrate(start_zone, amplitude, freq):
    """Vibrate from start_zone (0..9) to the bottom; amplitude 1..8, freq in Hz."""
    active = amp = 0
    for i in range(_clamp(start_zone, 0, 9), 10):
        active |= 1 << i
        amp |= (_clamp(amplitude, 1, 8) - 1) << (3 * i)
    return bytes([0x26, active & 0xFF, active >> 8, *amp.to_bytes(4, "little"), 0, 0, _clamp(freq, 1, 255), 0])


def feel_effect(feel):
    t = feel.get("type", "off")
    if t == "zones":
        return fx_zones(feel.get("zones", [0] * 10))
    if t == "smooth":
        s = feel.get("smooth", {})
        return fx_smooth(s.get("start", 0), s.get("force", 50))
    if t == "click":
        c = feel.get("click", {})
        return fx_click(c.get("start", 4), c.get("end", 6), c.get("force", 8))
    if t == "vibrate":
        v = feel.get("vibrate", {})
        return fx_vibrate(v.get("start", 0), v.get("amplitude", 5), v.get("freq", 40))
    return OFF


def reaction_effect(e):
    t = e.get("type", "kick")
    if t == "buzz":
        return fx_vibrate(e.get("start", 0), e.get("strength", 6), e.get("freq", 40))
    if t == "wall":
        return fx_smooth(e.get("start", 0) * 10, e.get("strength", 8) / 8 * 100)
    return fx_zones([0] * _clamp(e.get("start", 0), 0, 9) + [e.get("strength", 8)] * 10)


def remap(raw, out):
    """Physical trigger 0..255 -> output 0..1 using deadzone, throw, curve and max."""
    x = raw / 255
    dz = out.get("deadzone", 0) / 100
    full = max(dz + 0.05, out.get("full_at", 100) / 100)
    if x <= dz:
        return 0.0
    t = min(1.0, (x - dz) / (full - dz))
    t = t ** (2 ** (out.get("curve", 0) / 50))
    return t * out.get("max", 100) / 100


# ---------------------------------------------------------------- device

def output_report(common, bluetooth, seq):
    """Wrap the 47 common output bytes in a USB (0x02) or Bluetooth (0x31) report."""
    if not bluetooth:
        return bytes([0x02]) + common
    report = bytearray(78)
    report[0] = 0x31
    report[1] = (seq & 0x0F) << 4
    report[2] = 0x10
    report[3:50] = common
    report[74:78] = struct.pack("<I", zlib.crc32(bytes([0xA2]) + report[:74]))
    return bytes(report)


def output_common(left=None, right=None, lightbar=None, player_leds=None):
    common = bytearray(47)
    if right is not None:
        common[0] |= 0x04
        common[10:21] = right
    if left is not None:
        common[0] |= 0x08
        common[21:32] = left
    if lightbar is not None:
        common[1] |= 0x04
        common[44:47] = bytes(lightbar)
    if player_leds is not None:
        common[1] |= 0x10
        common[43] = player_leds
    return common


def find_device():
    for hr in sorted(glob.glob("/sys/class/hidraw/hidraw*")):
        hid_dir = os.path.realpath(os.path.join(hr, "device"))
        try:
            uevent = open(os.path.join(hid_dir, "uevent")).read()
        except OSError:
            continue
        info = dict(l.split("=", 1) for l in uevent.splitlines() if "=" in l)
        try:
            bus, vid, pid = (int(x, 16) for x in info.get("HID_ID", "").split(":"))
        except ValueError:
            continue
        if vid != SONY or pid not in MODELS:
            continue
        evdev = None
        for ev in glob.glob(os.path.join(hid_dir, "input", "input*", "event*")):
            name = open(os.path.join(os.path.dirname(ev), "name")).read().strip()
            if "Motion" not in name and "Touchpad" not in name:
                evdev = "/dev/input/" + os.path.basename(ev)
        return {"hidraw": "/dev/" + os.path.basename(hr), "evdev": evdev,
                "bluetooth": bus == 0x05, "model": MODELS[pid], "address": info.get("HID_UNIQ", "")}
    return None


class Device:
    def __init__(self, info):
        self.info = info
        self.fd = os.open(info["hidraw"], os.O_RDWR | os.O_NONBLOCK)
        self.seq = 0
        self.ev = None
        self.grabbed = False
        self.ff_id = -1
        if info["evdev"]:
            try:
                self.ev = os.open(info["evdev"], os.O_RDWR | os.O_NONBLOCK)
            except OSError:
                self.ev = None

    def read(self):
        """One input report; raises BlockingIOError when none is waiting."""
        return os.read(self.fd, 128)

    def send(self, left=None, right=None, lightbar=None, player_leds=None):
        common = output_common(left, right, lightbar, player_leds)
        os.write(self.fd, output_report(common, self.info["bluetooth"], self.seq))
        self.seq += 1

    def grab(self, on):
        """Hide the real controller from other programs while the virtual one is used."""
        if self.ev is None or on == self.grabbed:
            return
        try:
            fcntl.ioctl(self.ev, 0x40044590, 1 if on else 0)  # EVIOCGRAB
            self.grabbed = on
        except OSError:
            pass

    def rumble(self, strong, weak, ms=0):
        """Play (or with both 0, stop) rumble through the kernel's force feedback."""
        if self.ev is None:
            return
        if strong <= 0 and weak <= 0:
            if self.ff_id >= 0:
                os.write(self.ev, struct.pack("llHHi", 0, 0, 0x15, self.ff_id, 0))
            return
        eff = bytearray(struct.pack("HhHHHHH2xHH28x", 0x50, self.ff_id, 0, 0, 0, int(ms), 0,
                                    _clamp(strong * 0xFFFF, 0, 0xFFFF), _clamp(weak * 0xFFFF, 0, 0xFFFF)))
        fcntl.ioctl(self.ev, 0x40304580, eff)  # EVIOCSFF
        self.ff_id = struct.unpack_from("h", eff, 2)[0]
        os.write(self.ev, struct.pack("llHHi", 0, 0, 0x15, self.ff_id, 1))

    def close(self):
        try:
            self.send(OFF, OFF)
        except OSError:
            pass
        for fd in (self.fd, self.ev):
            if fd is not None:
                try:
                    os.close(fd)
                except OSError:
                    pass


if WINDOWS:
    from .winhid import Device, find_device  # noqa: E402,F811 (hidapi instead of hidraw)


def parse_input(r):
    """Decode a full input report (USB 0x01 or Bluetooth 0x31)."""
    if r[0] == 0x31 and len(r) >= 66:
        c = r[2:]
    elif r[0] == 0x01 and len(r) >= 64:
        c = r[1:]
    else:
        return None
    btn = c[7:11]
    pressed = [name for name, (i, bit) in BUTTON_BITS.items() if btn[i] & bit]
    pressed += DPAD.get(btn[0] & 0x0F, ())
    touches = []
    for p in (c[32:36], c[36:40]):
        if not p[0] & 0x80:
            touches.append({"id": p[0] & 0x7F, "x": p[1] | (p[2] & 0x0F) << 8, "y": p[2] >> 4 | p[3] << 4})
    status = c[52]
    return {
        "lx": c[0], "ly": c[1], "rx": c[2], "ry": c[3], "l2": c[4], "r2": c[5],
        "buttons": pressed,
        "gyro": struct.unpack_from("<3h", c, 15),
        "accel": struct.unpack_from("<3h", c, 21),
        "touches": touches,
        "battery": min((status & 0x0F) * 10 + 5, 100),
        "charging": {0: "discharging", 1: "charging", 2: "full"}.get(status >> 4, "unknown"),
    }


# ---------------------------------------------------------------- lighting

def light_color(cfg, preset, battery, t):
    """(r, g, b) for the light bar right now, or None to leave it alone."""
    mode = cfg.get("mode", "preset")
    if mode == "off":
        return (0, 0, 0)
    hexc = (preset or {}).get("lightbar", "#ffffff") if mode == "preset" else cfg.get("color", "#2f6bff")
    r, g, b = (int(hexc[i:i + 2], 16) / 255 for i in (1, 3, 5))
    effect = cfg.get("effect", "solid")
    speed = cfg.get("speed", 5)
    level = cfg.get("brightness", 100) / 100
    if effect == "breathe":
        level *= 0.15 + 0.85 * (0.5 - 0.5 * math.cos(t * speed * 0.6))
    elif effect == "rainbow":
        r, g, b = colorsys.hsv_to_rgb((t * speed * 0.03) % 1, 1, 1)
    elif effect == "battery":
        r, g, b = colorsys.hsv_to_rgb((battery or 0) / 100 * 0.33, 1, 1)
    return tuple(_clamp(c * level * 255, 0, 255) for c in (r, g, b))


PLAYER_LEDS = {"off": 0x00, "center": 0x04, "edges": 0x11, "all": 0x1F}


# ---------------------------------------------------------------- engine

class Engine(threading.Thread):
    def __init__(self):
        super().__init__(daemon=True)
        self.lock = threading.Lock()
        self.preset = None
        self.settings = {"lighting": {}, "output": {}}
        self.enabled = True
        self.override = None  # (left, right, until) from Feel It / Trigger Lab
        self.dev = None
        self.state = None
        self.out = {"l2": 0.0, "r2": 0.0}
        self.rate = 0
        self.light = None
        self.firing = {}
        self.vpad_error = None
        self.vpad_active = False
        self.game = GameTelemetry()
        self.rumble_level = (0.0, 0.0)  # what the game is asking the controller to rumble
        # A socket pair rather than a pipe so it can be selected on Windows too.
        self._wake_r, self._wake_w = socket.socketpair()
        self._wake_r.setblocking(False)
        self._wake_w.setblocking(False)
        self._refresh = True

    # ---- public API (any thread)

    def set_preset(self, preset):
        with self.lock:
            self.preset = preset
            self._refresh = True
        self._wake()

    def set_settings(self, settings):
        with self.lock:
            self.settings = settings
            self._refresh = True
        self._wake()

    def set_enabled(self, enabled):
        with self.lock:
            self.enabled = bool(enabled)
        self._wake()

    def set_override(self, left, right, seconds):
        with self.lock:
            self.override = None if left is None and right is None else \
                (left, right, time.monotonic() + seconds)
        self._wake()

    def rumble(self, strong, weak, ms):
        dev = self.dev
        if dev:
            try:
                dev.rumble(strong, weak, ms)
            except OSError:
                pass

    def snapshot(self):
        with self.lock:
            dev = self.dev
            return {
                "connected": dev is not None,
                "device": dict(dev.info) if dev else None,
                "input": self.state,
                "output": {k: round(v * 100, 1) for k, v in self.out.items()},
                "rate": self.rate,
                "light": "#%02x%02x%02x" % self.light[0] if self.light and dev else None,
                "player_leds": self.light[1] if self.light and dev else 0,
                "enabled": self.enabled,
                "firing": self.firing,
                "virtual": {"active": self.vpad_active, "error": self.vpad_error,
                            "uinput": uinput_available()},
                "telemetry": {"active": self.game.active, "source": self.game.source if self.game.active else None,
                              "gear": self.game.gear,
                              "speed": round(self.game.speed * 3.6) if self.game.active else None,
                              "ports": sorted(PORTS)},
                "rumble": round(max(self.rumble_level) * 100),
            }

    def _wake(self):
        try:
            self._wake_w.send(b"x")
        except BlockingIOError:
            pass

    # ---- engine thread

    def run(self):
        socks = []
        for port in PORTS:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            if not WINDOWS:  # on Windows this would let two programs split the packets
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                sock.bind(("0.0.0.0", port))
            except OSError:
                sock.close()
                continue
            sock.setblocking(False)
            socks.append((port, sock))
        while True:
            info = find_device()
            if not info:
                time.sleep(1)
                continue
            try:
                dev = Device(info)
            except OSError:
                time.sleep(2)
                continue
            with self.lock:
                self.dev = dev
                self._refresh = True
            vpad = None
            try:
                vpad = self._loop(dev, socks)
            except OSError as e:
                if e.errno not in (errno.ENODEV, errno.EIO, errno.EPIPE):
                    traceback.print_exc()
            except Exception:
                traceback.print_exc()
            finally:
                with self.lock:
                    self.dev = None
                    self.state = None
                    self.vpad_active = False
                dev.close()
            time.sleep(1)

    def _loop(self, dev, socks):
        sel = selectors.DefaultSelector()
        sel.register(dev.fd, selectors.EVENT_READ, "hid")
        sel.register(self._wake_r, selectors.EVENT_READ, "wake")
        for port, sock in socks:
            sel.register(sock, selectors.EVENT_READ, ("udp", port))

        vpad = None
        prev_buttons = set()
        hist = {"l2": [], "r2": []}
        until = {}            # reaction id -> monotonic time it stops
        combo_since = None
        sent, last_send = None, 0.0
        sent_light, last_light = None, 0.0
        count, count_t = 0, time.monotonic()
        t0 = time.monotonic()

        try:
            while True:
                now = time.monotonic()
                with self.lock:
                    preset = self.preset or {}
                    settings = self.settings
                    enabled = self.enabled
                    override = self.override
                    refresh, self._refresh = self._refresh, False
                want_vpad = settings.get("output", {}).get("virtual", False)

                # Bring the virtual controller up or down to match the setting.
                if want_vpad and vpad is None:
                    try:
                        vpad = VirtualPad()
                        sel.register(vpad.fd, selectors.EVENT_READ, "vpad")
                        dev.grab(True)
                        with self.lock:
                            self.vpad_active, self.vpad_error = True, None
                    except OSError as e:
                        vpad = None
                        with self.lock:
                            self.vpad_error = str(e)
                        want_vpad = False
                elif not want_vpad and vpad is not None:
                    sel.unregister(vpad.fd)
                    vpad.close()
                    vpad = None
                    dev.grab(False)
                    with self.lock:
                        self.vpad_active = False

                newly = set()
                for key, _ in sel.select(timeout=1 / 120):
                    if key.data == "wake":
                        try:
                            self._wake_r.recv(64)
                        except BlockingIOError:
                            pass
                    elif isinstance(key.data, tuple):
                        try:
                            while True:
                                self.game.feed(key.data[1], key.fileobj.recv(2048))
                        except BlockingIOError:
                            pass
                    elif key.data == "vpad":
                        for strong, weak in vpad.read_rumble():
                            self.rumble_level = (strong, weak)
                            try:
                                dev.rumble(strong, weak)
                            except OSError:
                                pass
                    else:
                        while True:
                            try:
                                r = dev.read()
                            except BlockingIOError:
                                break
                            if not r:
                                raise OSError(errno.ENODEV, "gone")
                            st = parse_input(r)
                            if not st:
                                continue
                            count += 1
                            buttons = set(st["buttons"])
                            newly |= buttons - prev_buttons
                            prev_buttons = buttons
                            hist["l2"].append((now, st["l2"]))
                            hist["r2"].append((now, st["r2"]))
                            trig = preset.get("triggers", {})
                            o_l2 = remap(st["l2"], trig.get("l2", {}).get("output", {}))
                            o_r2 = remap(st["r2"], trig.get("r2", {}).get("output", {}))
                            with self.lock:
                                self.state = st
                                self.out = {"l2": o_l2, "r2": o_r2}
                            if vpad:
                                vpad.update(st, o_l2, o_r2, settings.get("output", {}).get("map", {}))

                if now - count_t >= 1.0:
                    with self.lock:
                        self.rate = round(count / (now - count_t))
                    count, count_t = 0, now

                st = self.state

                # Hold L3 + R3 for a second to toggle effects.
                if st and "l3" in st["buttons"] and "r3" in st["buttons"]:
                    combo_since = combo_since or now
                    if now - combo_since > 1.0:
                        with self.lock:
                            self.enabled = enabled = not enabled
                        combo_since = float("inf")
                else:
                    combo_since = None

                for k in hist:
                    hist[k] = [(t, v) for t, v in hist[k] if now - t < 0.5]
                telemetry = preset.get("telemetry", "none") != "none" and self.game.active
                gear_now = self.game.take_gear_change()

                effects, firing = {}, {}
                for side in ("l2", "r2"):
                    tcfg = preset.get("triggers", {}).get(side, {})
                    fx = feel_effect(tcfg.get("feel", {}))
                    pull = st[side] if st else 0
                    firing[side] = None
                    for r in tcfg.get("reactions", []) if tcfg.get("reactions_on", True) else []:
                        if not r.get("enabled", True):
                            continue
                        rid = r.get("id")
                        if self._fires(r, side, pull, hist[side], newly, telemetry, gear_now, now):
                            dur = r.get("effect", {}).get("duration_ms", 60) / 1000
                            until[rid] = now + dur if r.get("when") in INSTANT else now + 0.05
                        if now < until.get(rid, 0):
                            eff = r.get("effect", {})
                            if r.get("when") == "rumble" and r.get("rumble", {}).get("scale"):
                                eff = dict(eff, strength=max(1, round(eff.get("strength", 8) * max(self.rumble_level))))
                            fx = reaction_effect(eff)
                            firing[side] = rid
                            break
                    effects[side] = fx
                with self.lock:
                    self.firing = firing

                if override and now < override[2]:
                    left, right = override[0] or OFF, override[1] or OFF
                elif not enabled or not preset:
                    left = right = OFF
                else:
                    if override:
                        with self.lock:
                            self.override = None
                    left, right = effects["l2"], effects["r2"]

                if (left, right) != sent or now - last_send > 1.0 or refresh:
                    dev.send(left, right)
                    sent, last_send = (left, right), now

                lcfg = settings.get("lighting", {})
                if now - last_light > 1 / 30 or refresh:
                    color = light_color(lcfg, preset, st["battery"] if st else None, now - t0)
                    leds = PLAYER_LEDS.get(lcfg.get("player_leds", "center"), 0x04)
                    if (color, leds) != sent_light or now - last_light > 2.0 or refresh:
                        dev.send(lightbar=color, player_leds=leds)
                        sent_light = (color, leds)
                        with self.lock:
                            self.light = (color, leds)
                    last_light = now
        finally:
            if vpad:
                vpad.close()
                dev.grab(False)

    def _fires(self, r, side, pull, hist, newly, telemetry, gear_now, now):
        when = r.get("when")
        if when == "buttons":
            return bool(newly & set(r.get("buttons", [])))
        if when == "slam":
            s = r.get("slam", {})
            window = s.get("within", 200) / 1000
            recent = [v for t, v in hist if now - t <= window]
            return (pull >= s.get("to", 85) * 2.55 and recent
                    and min(recent) <= s.get("from", 30) * 2.55)
        if when == "held":
            return pull >= r.get("held", {}).get("above", 90) * 2.55
        if when == "rumble":
            return max(self.rumble_level) * 100 >= r.get("rumble", {}).get("above", 30)
        if when in GAME_EVENTS and not telemetry:
            return False
        if when == "gear":
            return gear_now
        if when == "abs":
            return self.game.abs
        if when == "wheelspin":
            return self.game.wheelspin
        if when == "redline":
            return self.game.redline
        return False
