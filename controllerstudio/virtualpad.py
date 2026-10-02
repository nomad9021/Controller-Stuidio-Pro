"""A virtual Xbox 360-style controller (via /dev/uinput) that games see instead
of the real DualSense, so trigger throw, response and button mapping can be
changed digitally. Rumble sent to it is handed back to the engine.
"""
import fcntl
import os
import struct

EV_SYN, EV_KEY, EV_ABS, EV_FF, EV_UINPUT = 0x00, 0x01, 0x03, 0x15, 0x0101
FF_RUMBLE = 0x50
UI_SET_EVBIT, UI_SET_KEYBIT, UI_SET_ABSBIT, UI_SET_FFBIT = 0x40045564, 0x40045565, 0x40045567, 0x4004556B
UI_DEV_SETUP, UI_ABS_SETUP, UI_DEV_CREATE, UI_DEV_DESTROY = 0x405C5503, 0x401C5504, 0x5501, 0x5502
UI_BEGIN_FF_UPLOAD, UI_END_FF_UPLOAD = 0xC06855C8, 0x406855C9
UI_BEGIN_FF_ERASE, UI_END_FF_ERASE = 0xC00C55CA, 0x400C55CB

# Xbox-style key codes, in the order xpad exposes them.
KEYS = {
    "cross": 0x130, "circle": 0x131, "square": 0x133, "triangle": 0x134,  # A B X Y
    "l1": 0x136, "r1": 0x137, "create": 0x13A, "options": 0x13B, "ps": 0x13C,
    "l3": 0x13D, "r3": 0x13E,
}
ABS_X, ABS_Y, ABS_Z, ABS_RX, ABS_RY, ABS_RZ, ABS_HAT0X, ABS_HAT0Y = 0, 1, 2, 3, 4, 5, 0x10, 0x11
TRIGGER_MAX = 1023
NAME = b"Controller Studio Pro Virtual Controller"


def uinput_available():
    return os.access("/dev/uinput", os.R_OK | os.W_OK)


class VirtualPad:
    def __init__(self):
        self.fd = os.open("/dev/uinput", os.O_RDWR | os.O_NONBLOCK)
        try:
            for ev in (EV_KEY, EV_ABS, EV_FF, EV_SYN):
                fcntl.ioctl(self.fd, UI_SET_EVBIT, ev)
            for code in KEYS.values():
                fcntl.ioctl(self.fd, UI_SET_KEYBIT, code)
            fcntl.ioctl(self.fd, UI_SET_FFBIT, FF_RUMBLE)
            for code, lo, hi, fuzz, flat in (
                (ABS_X, -32768, 32767, 16, 128), (ABS_Y, -32768, 32767, 16, 128),
                (ABS_RX, -32768, 32767, 16, 128), (ABS_RY, -32768, 32767, 16, 128),
                (ABS_Z, 0, TRIGGER_MAX, 0, 0), (ABS_RZ, 0, TRIGGER_MAX, 0, 0),
                (ABS_HAT0X, -1, 1, 0, 0), (ABS_HAT0Y, -1, 1, 0, 0),
            ):
                fcntl.ioctl(self.fd, UI_SET_ABSBIT, code)
                fcntl.ioctl(self.fd, UI_ABS_SETUP, struct.pack("H2x6i", code, 0, lo, hi, fuzz, flat, 0))
            # BUS_USB, Microsoft Xbox 360 pad ids so games and SDL map it automatically.
            fcntl.ioctl(self.fd, UI_DEV_SETUP, struct.pack("4H80sI", 0x03, 0x045E, 0x028E, 0x0114, NAME, 16))
            fcntl.ioctl(self.fd, UI_DEV_CREATE)
        except OSError:
            os.close(self.fd)
            raise
        self.last = {}
        self.effects = {}  # effect id -> (strong, weak)

    def _emit(self, events):
        buf = b"".join(struct.pack("llHHi", 0, 0, t, c, v) for t, c, v in events)
        os.write(self.fd, buf + struct.pack("llHHi", 0, 0, EV_SYN, 0, 0))

    def update(self, st, l2, r2, mapping):
        """Mirror the real controller, with remapped triggers and extra buttons."""
        pressed = set(st["buttons"])
        for src, dst in (mapping or {}).items():
            if dst and src in pressed:
                pressed.add(dst)
        stick = lambda v: max(-32768, min(32767, int((v - 128) * 258)))
        state = {
            (EV_ABS, ABS_X): stick(st["lx"]), (EV_ABS, ABS_Y): stick(st["ly"]),
            (EV_ABS, ABS_RX): stick(st["rx"]), (EV_ABS, ABS_RY): stick(st["ry"]),
            (EV_ABS, ABS_Z): int(l2 * TRIGGER_MAX), (EV_ABS, ABS_RZ): int(r2 * TRIGGER_MAX),
            (EV_ABS, ABS_HAT0X): ("right" in pressed) - ("left" in pressed),
            (EV_ABS, ABS_HAT0Y): ("down" in pressed) - ("up" in pressed),
        }
        for name, code in KEYS.items():
            down = name in pressed or (name == "create" and "touchpad" in pressed)
            state[(EV_KEY, code)] = int(down)
        changed = [(t, c, v) for (t, c), v in state.items() if self.last.get((t, c)) != v]
        if changed:
            self._emit(changed)
            self.last = state

    def read_rumble(self):
        """Handle force-feedback traffic from games; yields (strong, weak) to play."""
        while True:
            try:
                data = os.read(self.fd, 24 * 16)
            except BlockingIOError:
                return
            for off in range(0, len(data) - 23, 24):
                _, _, typ, code, val = struct.unpack_from("llHHi", data, off)
                if typ == EV_UINPUT and code == 1:  # UI_FF_UPLOAD
                    up = bytearray(struct.pack("Ii96x", val, 0))
                    fcntl.ioctl(self.fd, UI_BEGIN_FF_UPLOAD, up)
                    etype, eid = struct.unpack_from("Hh", up, 8)
                    if etype == FF_RUMBLE:
                        strong, weak = struct.unpack_from("HH", up, 8 + 16)
                        self.effects[eid] = (strong / 0xFFFF, weak / 0xFFFF)
                    struct.pack_into("i", up, 4, 0)
                    fcntl.ioctl(self.fd, UI_END_FF_UPLOAD, bytes(up))
                elif typ == EV_UINPUT and code == 2:  # UI_FF_ERASE
                    er = bytearray(struct.pack("IiI", val, 0, 0))
                    fcntl.ioctl(self.fd, UI_BEGIN_FF_ERASE, er)
                    self.effects.pop(struct.unpack_from("I", er, 8)[0], None)
                    fcntl.ioctl(self.fd, UI_END_FF_ERASE, bytes(er))
                elif typ == EV_FF and code in self.effects:
                    yield self.effects[code] if val else (0.0, 0.0)

    def close(self):
        try:
            fcntl.ioctl(self.fd, UI_DEV_DESTROY)
        except OSError:
            pass
        os.close(self.fd)
