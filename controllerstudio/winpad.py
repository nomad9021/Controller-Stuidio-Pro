"""The virtual Xbox 360 controller on Windows, through the ViGEmBus driver
(vgamepad). Same interface as virtualpad.VirtualPad: rumble sent to it by
games is queued and signalled on a socket for the engine's selector.
"""
import socket
import threading
import time

XUSB = {
    "cross": 0x1000, "circle": 0x2000, "square": 0x4000, "triangle": 0x8000,  # A B X Y
    "l1": 0x0100, "r1": 0x0200, "create": 0x0020, "options": 0x0010, "ps": 0x0400,
    "l3": 0x0040, "r3": 0x0080,
    "up": 0x0001, "down": 0x0002, "left": 0x0004, "right": 0x0008,
}

_checked = (0.0, False)


def uinput_available():
    """True when the ViGEmBus driver is installed (checked at most every 5 s)."""
    global _checked
    t, ok = _checked
    if ok or time.monotonic() - t < 5:
        return ok
    try:
        from vgamepad.win.vigem_client import vigem_alloc, vigem_connect, vigem_disconnect, vigem_free
        client = vigem_alloc()
        ok = vigem_connect(client) == 0x20000000  # VIGEM_ERROR_NONE
        if ok:
            vigem_disconnect(client)
        vigem_free(client)
    except Exception:
        ok = False
    _checked = (time.monotonic(), ok)
    return ok


class VirtualPad:
    def __init__(self):
        try:
            import vgamepad
            self.pad = vgamepad.VX360Gamepad()
        except Exception as e:  # missing driver shows up as an assertion or a DLL error
            raise OSError(f"Couldn't create the virtual controller: {e or 'ViGEmBus is not installed'}") from e
        self.fd, self._signal = socket.socketpair()
        self.fd.setblocking(False)
        self._signal.setblocking(False)
        self.lock = threading.Lock()
        self.rumble = None
        self.last = None
        self.pad.register_notification(self._notify)

    def _notify(self, client, target, large_motor, small_motor, led_number, user_data):
        with self.lock:
            self.rumble = (large_motor / 255, small_motor / 255)
        try:
            self._signal.send(b"x")
        except OSError:
            pass

    def update(self, st, l2, r2, mapping):
        """Mirror the real controller, with remapped triggers and extra buttons."""
        pressed = set(st["buttons"])
        for src, dst in (mapping or {}).items():
            if dst and src in pressed:
                pressed.add(dst)
        if "touchpad" in pressed:
            pressed.add("create")
        stick = lambda v: max(-32768, min(32767, int((v - 128) * 258)))
        state = (
            sum(bit for name, bit in XUSB.items() if name in pressed),
            stick(st["lx"]), -1 - stick(st["ly"]), stick(st["rx"]), -1 - stick(st["ry"]),  # XInput Y points up
            int(l2 * 255), int(r2 * 255),
        )
        if state == self.last:
            return
        self.last = state
        r = self.pad.report
        r.wButtons, r.sThumbLX, r.sThumbLY, r.sThumbRX, r.sThumbRY = state[:5]
        self.pad.left_trigger(state[5])
        self.pad.right_trigger(state[6])
        self.pad.update()

    def read_rumble(self):
        """Yields the (strong, weak) rumble the game most recently asked for."""
        try:
            while self.fd.recv(256):
                pass
        except BlockingIOError:
            pass
        with self.lock:
            rumble, self.rumble = self.rumble, None
        if rumble is not None:
            yield rumble

    def close(self):
        try:
            self.pad.unregister_notification()
            del self.pad
        except Exception:
            pass
        for s in (self.fd, self._signal):
            s.close()
