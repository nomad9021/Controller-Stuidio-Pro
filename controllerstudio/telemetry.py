"""Live game data over UDP, normalised to what trigger reactions need:
gear changes, wheel lock (ABS), wheelspin and the rev limiter.

Supported:
  * Forza Horizon 4/5, Forza Motorsport 7 / 2023  - "Data Out", port 5300
  * F1 22 / 23 / 24 / 25                           - UDP telemetry, port 20777
  * DiRT Rally 2.0, DiRT 4, GRID (Codemasters)     - extradata=3, port 20777
  * BeamNG.drive, Live for Speed                   - OutGauge, port 4444
"""
import struct
import time

PORTS = {5300: "Forza", 20777: "Codemasters / F1", 4444: "OutGauge"}


class GameTelemetry:
    def __init__(self):
        self.last = 0.0
        self.source = None
        self.gear = None
        self.gear_changed = False
        self.abs = self.wheelspin = self.redline = False
        self.speed = 0.0          # m/s
        self._f1 = {}             # partial F1 state between packet types

    @property
    def active(self):
        return time.monotonic() - self.last < 1.0

    def _update(self, source, gear, speed, abs_, spin, red):
        self.last = time.monotonic()
        self.source = source
        if self.gear is not None and gear != self.gear and gear not in (0, None):
            self.gear_changed = True
        self.gear = gear
        self.speed = speed
        self.abs, self.wheelspin, self.redline = bool(abs_), bool(spin), bool(red)

    def take_gear_change(self):
        changed, self.gear_changed = self.gear_changed, False
        return changed

    def feed(self, port, pkt):
        try:
            if port == 5300:
                self._forza(pkt)
            elif port == 20777:
                if len(pkt) in (256, 264):
                    self._codemasters(pkt)
                else:
                    self._f1_packet(pkt)
            elif port == 4444:
                self._outgauge(pkt)
        except struct.error:
            pass

    # ---- Forza "Data Out" (sled 232 / dash 311 / Horizon 324 / FM2023 331)
    def _forza(self, pkt):
        if len(pkt) < 311:
            return
        if struct.unpack_from("<i", pkt, 0)[0] == 0:   # IsRaceOn = 0: menus / paused
            self.last = 0.0
            return
        dash = 244 if len(pkt) == 324 else 232        # Horizon inserts 12 bytes
        max_rpm, _, rpm = struct.unpack_from("<3f", pkt, 8)
        slip = struct.unpack_from("<4f", pkt, 84)       # FL FR RL RR
        speed = struct.unpack_from("<f", pkt, dash + 12)[0]
        accel, brake, gear = pkt[dash + 71], pkt[dash + 72], pkt[dash + 75]
        self._update("Forza", gear, speed,
                     brake > 100 and speed > 3 and max(abs(s) for s in slip) > 1.0,
                     accel > 100 and max(abs(slip[2]), abs(slip[3])) > 1.2,
                     max_rpm > 0 and rpm > 0.97 * max_rpm)

    # ---- Codemasters legacy "extradata=3": 64-66 little-endian floats
    def _codemasters(self, pkt):
        f = struct.unpack_from("<64f", pkt, 0)
        speed = f[7]
        rl, rr, fl, fr = f[25:29]
        throttle, brake, gear = f[29], f[31], int(round(f[33]))
        rpm, max_rpm = f[37] * 10, f[63] * 10
        self._update("DiRT / GRID", gear, speed,
                     brake > 0.4 and speed > 3 and min(fl, fr) < speed * 0.75,
                     throttle > 0.4 and speed > 1 and max(rl, rr) > speed * 1.25 + 1,
                     max_rpm > 0 and rpm > 0.96 * max_rpm)

    # ---- F1 22-25: car telemetry + wheel slip from the motion packets
    def _f1_packet(self, pkt):
        fmt = struct.unpack_from("<H", pkt, 0)[0]
        if fmt == 2022:
            H, pid, player = 24, pkt[5], pkt[22]
        elif fmt >= 2023:
            H, pid, player = 29, pkt[6], pkt[27]
        else:
            return
        s = self._f1
        if pid == 6:                                    # car telemetry, 60 bytes per car
            o = H + player * 60
            kmh, throttle, _, brake = struct.unpack_from("<Hfff", pkt, o)
            gear, rev_pct = struct.unpack_from("<b", pkt, o + 15)[0], pkt[o + 19]
            slip = s.get("slip", (0, 0, 0, 0))          # RL RR FL FR
            self._update(f"F1 {fmt}", gear, kmh / 3.6,
                         brake > 0.5 and kmh > 10 and max(abs(slip[2]), abs(slip[3])) > 0.25,
                         throttle > 0.5 and max(abs(slip[0]), abs(slip[1])) > 0.25,
                         rev_pct >= 92)
        elif (fmt >= 2023 and pid == 13) or (fmt == 2022 and pid == 0):
            off = H + 64 if fmt >= 2023 else H + 22 * 60 + 64
            s["slip"] = struct.unpack_from("<4f", pkt, off)

    # ---- OutGauge (BeamNG.drive, Live for Speed)
    def _outgauge(self, pkt):
        if len(pkt) < 92:
            return
        gear = pkt[10] - 1                               # 0=R,1=N,2=1st -> -1/0/1...
        speed = struct.unpack_from("<f", pkt, 12)[0]
        lights = struct.unpack_from("<I", pkt, 44)[0]
        self._update("BeamNG / LFS", gear, speed,
                     lights & (1 << 10),                 # DL_ABS
                     lights & (1 << 4),                  # DL_TC (traction control = wheelspin)
                     lights & (1 << 0))                  # DL_SHIFT
