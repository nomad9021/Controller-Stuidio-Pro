"""The DualSense on Windows, through hidapi instead of hidraw.

Windows has no kernel driver doing setup for us, so this also asks a Bluetooth
controller for full input reports and takes over the light bar. Rumble goes
through the controller's own motor bytes because there is no evdev force
feedback. Reads happen on a thread that signals a socket, so the engine can
keep waiting on everything with one selector.
"""
import collections
import errno
import socket
import threading

import hid

from .engine import MODELS, OFF, SONY, output_common, output_report

BT_HID_UUID = "00001124-0000-1000-8000-00805f9b34fb"  # in the device path of Bluetooth HID devices


def find_device():
    for d in hid.enumerate(SONY):
        if d.get("product_id") not in MODELS:
            continue
        # The DualSense has a single HID collection: Generic Desktop / Game Pad.
        if d.get("usage_page") not in (None, 0, 1) or d.get("usage") not in (None, 0, 5):
            continue
        path = d["path"]
        text = path.decode(errors="replace") if isinstance(path, bytes) else str(path)
        return {"hidraw": path, "evdev": None, "bluetooth": BT_HID_UUID in text.lower(),
                "model": MODELS[d["product_id"]], "address": d.get("serial_number") or ""}
    return None


class Device:
    def __init__(self, info):
        self.info = dict(info, hidraw=None)  # the raw path isn't JSON-friendly
        self.seq = 0
        self.grabbed = False
        self.lock = threading.Lock()
        self.reports = collections.deque(maxlen=64)
        self.gone = False
        self._rumble_timer = None
        self.h = hid.device()
        try:
            self.h.open_path(info["hidraw"])
        except (OSError, ValueError) as e:
            raise OSError(errno.EACCES, str(e)) from e
        self.fd, self._signal = socket.socketpair()
        self.fd.setblocking(False)
        self._signal.setblocking(False)
        if self.info["bluetooth"]:
            try:  # reading the calibration report switches Bluetooth to full 0x31 reports
                self.h.get_feature_report(0x05, 41)
            except (OSError, ValueError):
                pass
        common = bytearray(47)
        common[38] = 0x02  # let us set the light bar...
        common[41] = 0x02  # ...and fade out the default blue
        self._write(common)
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _read_loop(self):
        while not self.gone:
            try:
                data = self.h.read(128, 250)
            except (OSError, ValueError):
                data = None
            if data is None:
                self.gone = True
            elif not data:
                continue
            else:
                r = bytes(data)
                # The first full report tells us for sure how it is connected.
                if r[0] == 0x31 and len(r) >= 66:
                    self.info["bluetooth"] = True
                elif r[0] == 0x01 and len(r) >= 64:
                    self.info["bluetooth"] = False
                self.reports.append(r)
            try:
                self._signal.send(b"x")
            except OSError:
                return

    def read(self):
        """One input report; raises BlockingIOError when none is waiting."""
        if not self.reports:
            try:
                while self.fd.recv(256):
                    pass
            except BlockingIOError:
                pass
        if self.reports:
            return self.reports.popleft()
        if self.gone:
            raise OSError(errno.ENODEV, "controller disconnected")
        raise BlockingIOError

    def _write(self, common):
        with self.lock:
            self.h.write(output_report(common, self.info["bluetooth"], self.seq))
            self.seq += 1

    def send(self, left=None, right=None, lightbar=None, player_leds=None):
        self._write(output_common(left, right, lightbar, player_leds))

    def grab(self, on):
        """Windows can't hide a device from other programs without HidHide."""

    def rumble(self, strong, weak, ms=0):
        """Play (or with both 0, stop) rumble; with ms it stops by itself."""
        if self._rumble_timer:
            self._rumble_timer.cancel()
            self._rumble_timer = None
        common = bytearray(47)
        common[0] = 0x01 | 0x02  # classic rumble instead of haptics
        common[2] = max(0, min(255, int(weak * 255)))    # right, small motor
        common[3] = max(0, min(255, int(strong * 255)))  # left, big motor
        self._write(common)
        if ms and (strong > 0 or weak > 0):
            self._rumble_timer = threading.Timer(ms / 1000, self._stop_rumble)
            self._rumble_timer.daemon = True
            self._rumble_timer.start()

    def _stop_rumble(self):
        try:
            self.rumble(0, 0)
        except (OSError, ValueError):
            pass

    def close(self):
        try:
            self.rumble(0, 0)
            self.send(OFF, OFF)
        except (OSError, ValueError):
            pass
        self.gone = True
        self._reader.join(timeout=1)
        for s in (self.fd, self._signal):
            s.close()
        try:
            self.h.close()
        except (OSError, ValueError):
            pass
