"""Built-in presets, the user's edits, and app-wide settings (~/.config/controller-studio-pro/)."""
import copy
import json
import os
import re
import threading

_CONFIG_HOME = os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config"))
CONFIG_DIR = os.path.join(_CONFIG_HOME, "controller-studio-pro")
STORE = os.path.join(CONFIG_DIR, "presets.json")
_OLD_STORE = os.path.join(_CONFIG_HOME, "pad-studio", "presets.json")  # from before the rename
VERSION = 2


def _progressive(top, start=2):
    n = 10 - start
    return [0] * start + [max(1, round(top * (i + 1) / n)) for i in range(n)]


def trigger(feel_type="off", zones=None, smooth=None, click=None, vibrate=None, reactions=(), output=None):
    return {
        "feel": {
            "type": feel_type,
            "zones": zones or [0] * 10,
            "smooth": smooth or {"start": 10, "force": 40},
            "click": click or {"start": 4, "end": 6, "force": 8},
            "vibrate": vibrate or {"start": 0, "amplitude": 5, "freq": 40},
        },
        "reactions": list(reactions),
        "output": output or {"deadzone": 0, "full_at": 100, "curve": 0, "max": 100},
    }


def reaction(rid, when, effect, **extra):
    r = {"id": rid, "enabled": True, "when": when, "buttons": [], "effect": effect,
         "slam": {"from": 30, "to": 85, "within": 200}, "held": {"above": 90},
         "rumble": {"above": 30, "scale": True}}
    r.update(extra)
    return r


def kick(strength, ms, start=0):
    return {"type": "kick", "strength": strength, "duration_ms": ms, "start": start, "freq": 45}


def buzz(strength, freq, start=0, ms=600):
    return {"type": "buzz", "strength": strength, "freq": freq, "start": start, "duration_ms": ms}


BUILTIN = [
    {
        "id": "motorfest", "name": "The Crew Motorfest",
        "summary": "L2 stiffens and chatters when slammed; R2 is free with a hard kick on every shift.",
        "telemetry": "none", "lightbar": "#ff5a1f",
        "triggers": {
            "l2": trigger("zones", zones=_progressive(6),
                          reactions=[reaction("slam", "slam", buzz(7, 28, start=4, ms=600)),
                                     dict(reaction("rumble", "rumble", buzz(6, 30, start=3)), enabled=False)]),
            "r2": trigger("off", reactions=[reaction("shift", "buttons", kick(7, 50), buttons=["circle", "square"])]),
        },
    },
    {
        "id": "forza-horizon", "name": "Forza Horizon 5",
        "summary": "Uses live car data: real ABS lock-up, a kick on every gear change, wheelspin buzz.",
        "telemetry": "game", "lightbar": "#e8178a",
        "triggers": {
            "l2": trigger("zones", zones=_progressive(5), reactions=[reaction("abs", "abs", buzz(6, 30, start=4))]),
            "r2": trigger("off", reactions=[reaction("gear", "gear", kick(6, 60)),
                                            reaction("spin", "wheelspin", buzz(4, 60, start=3))]),
        },
    },
    {
        "id": "forza-motorsport", "name": "Forza Motorsport",
        "summary": "Sim-style: a firm wall on L2, light weight on R2, redline and wheelspin cues.",
        "telemetry": "game", "lightbar": "#2f6bff",
        "triggers": {
            "l2": trigger("zones", zones=[0, 0, 1, 2, 4, 6, 8, 8, 8, 8],
                          reactions=[reaction("abs", "abs", buzz(7, 32, start=5))]),
            "r2": trigger("smooth", smooth={"start": 5, "force": 15},
                          reactions=[reaction("gear", "gear", kick(7, 50)),
                                     reaction("spin", "wheelspin", buzz(5, 55, start=3)),
                                     reaction("redline", "redline", buzz(2, 80, start=5))]),
        },
    },
    {
        "id": "f1", "name": "F1 25",
        "summary": "Live F1 data: brake lock-up chatter, a kick on every upshift, a buzz on the rev lights.",
        "telemetry": "game", "lightbar": "#e10600",
        "triggers": {
            "l2": trigger("zones", zones=[0, 0, 2, 3, 4, 6, 8, 8, 8, 8], reactions=[reaction("abs", "abs", buzz(7, 35, start=5))]),
            "r2": trigger("smooth", smooth={"start": 5, "force": 10},
                          reactions=[reaction("gear", "gear", kick(7, 45)),
                                     reaction("spin", "wheelspin", buzz(5, 60, start=3)),
                                     reaction("redline", "redline", buzz(2, 90, start=6))]),
        },
    },
    {
        "id": "dirt-rally", "name": "DiRT Rally 2.0",
        "summary": "Codemasters data (also DiRT 4 and GRID): loose-surface wheelspin, lock-ups and shift kicks.",
        "telemetry": "game", "lightbar": "#ffb000",
        "triggers": {
            "l2": trigger("zones", zones=_progressive(5), reactions=[reaction("abs", "abs", buzz(6, 25, start=4))]),
            "r2": trigger("off", reactions=[reaction("gear", "gear", kick(7, 50)),
                                            reaction("spin", "wheelspin", buzz(4, 45, start=2))]),
        },
    },
    {
        "id": "beamng", "name": "BeamNG.drive",
        "summary": "OutGauge data (also Live for Speed): ABS and traction-control lights drive the triggers.",
        "telemetry": "game", "lightbar": "#ff7a00",
        "triggers": {
            "l2": trigger("zones", zones=_progressive(6), reactions=[reaction("abs", "abs", buzz(6, 30, start=4))]),
            "r2": trigger("off", reactions=[reaction("gear", "gear", kick(6, 55)),
                                            reaction("spin", "wheelspin", buzz(4, 55, start=3))]),
        },
    },
    {
        "id": "r6", "name": "Rainbow Six Siege",
        "summary": "Light aim weight on L2, a click wall on R2, and recoil kicks driven by the game's own rumble.",
        "telemetry": "none", "lightbar": "#4aa3ff",
        "triggers": {
            "l2": trigger("smooth", smooth={"start": 5, "force": 20}),
            "r2": trigger("click", click={"start": 3, "end": 5, "force": 6},
                          reactions=[reaction("recoil", "rumble", buzz(8, 14, start=3), rumble={"above": 25, "scale": True})]),
        },
    },
    {
        "id": "shooter", "name": "Shooter",
        "summary": "Light weight on aim, a crisp click-through wall on the trigger.",
        "telemetry": "none", "lightbar": "#3ccf6e",
        "triggers": {
            "l2": trigger("smooth", smooth={"start": 5, "force": 25}),
            "r2": trigger("click", click={"start": 4, "end": 6, "force": 8}),
        },
    },
    {
        "id": "off", "name": "Off", "summary": "Triggers feel like a normal controller.",
        "telemetry": "none", "lightbar": "#ffffff",
        "triggers": {"l2": trigger(), "r2": trigger()},
    },
]
BUILTIN_IDS = {p["id"] for p in BUILTIN}

DEFAULT_SETTINGS = {
    "lighting": {"mode": "preset", "color": "#2f6bff", "effect": "solid", "speed": 5,
                 "brightness": 100, "player_leds": "center"},
    "output": {"virtual": False, "map": {"paddle_left": "", "paddle_right": "", "fn_left": "", "fn_right": ""}},
    "app": {"glass": 70},
}


def migrate_v1(p):
    """Old presets had brake/gas sections; convert them to generic L2/R2 triggers."""
    if "triggers" in p or "brake" not in p:
        return p
    def conv(old, reactions):
        mode = old.get("mode", "off")
        if mode == "weapon":
            w = old.get("weapon", {})
            return trigger("click", click={"start": w.get("start", 4), "end": w.get("end", 6),
                                           "force": w.get("strength", 8)}, reactions=reactions)
        curve = old.get("curve", [0] * 10)
        return trigger("zones" if mode == "curve" and any(curve) else "off", zones=curve, reactions=reactions)
    b, g = p["brake"], p["gas"]
    l2r, r2r = [], []
    a = b.get("abs", {})
    if a.get("enabled"):
        when = "abs" if p.get("telemetry") == "forza" else "slam"
        l2r.append(reaction("abs", when, buzz(a.get("strength", 7), a.get("freq", 28),
                                              a.get("start_zone", 4), a.get("duration_ms", 600))))
    bl = g.get("blip", {})
    if bl.get("enabled"):
        eff = kick(bl.get("strength", 7), bl.get("duration_ms", 50))
        if bl.get("style") == "buzz":
            eff = buzz(bl.get("strength", 7), bl.get("freq", 45), ms=bl.get("duration_ms", 50))
        if p.get("telemetry") == "forza":
            r2r.append(reaction("gear", "gear", eff))
        sh = [x for x in (p.get("shift", {}).get("up"), p.get("shift", {}).get("down")) if x]
        if sh:
            r2r.append(reaction("shift", "buttons", eff, buttons=sh))
    for key, when in (("wheelspin", "wheelspin"), ("redline", "redline")):
        s = g.get(key, {})
        if s.get("enabled"):
            r2r.append(reaction(key, when, buzz(s.get("strength", 3), s.get("freq", 60), 3)))
    return {"id": p["id"], "name": p["name"], "summary": p.get("summary", ""),
            "telemetry": p.get("telemetry", "none"), "lightbar": p.get("lightbar", "#ffffff"),
            "triggers": {"l2": conv(b, l2r), "r2": conv(g, r2r)}}


class PresetStore:
    def __init__(self):
        self.lock = threading.Lock()
        self.data = {"active": "motorfest", "overrides": {}, "custom": [], "telemetry_port": 5300,
                     "settings": copy.deepcopy(DEFAULT_SETTINGS)}
        for path in (STORE, _OLD_STORE):
            try:
                with open(path) as f:
                    self.data.update(json.load(f))
                break
            except (OSError, ValueError):
                continue
        if self.data.get("version", 1) < VERSION:
            self.data["overrides"] = {k: migrate_v1(v) for k, v in self.data["overrides"].items()}
            self.data["custom"] = [migrate_v1(p) for p in self.data["custom"]]
            self.data["version"] = VERSION
            self._save()
        for k, v in DEFAULT_SETTINGS.items():
            for kk, vv in v.items():
                self.data["settings"].setdefault(k, {}).setdefault(kk, copy.deepcopy(vv))

    def _save(self):
        os.makedirs(CONFIG_DIR, exist_ok=True)
        tmp = STORE + ".tmp"
        with open(tmp, "w") as f:
            json.dump(self.data, f, indent=2)
        os.replace(tmp, STORE)

    # ---- settings

    @property
    def settings(self):
        with self.lock:
            return copy.deepcopy(self.data["settings"])

    def update_settings(self, patch):
        with self.lock:
            for section, values in patch.items():
                if section in DEFAULT_SETTINGS and isinstance(values, dict):
                    self.data["settings"][section].update(values)
            self._save()
            return copy.deepcopy(self.data["settings"])

    # ---- presets

    def all(self):
        with self.lock:
            out = []
            for p in BUILTIN:
                cur = copy.deepcopy(self.data["overrides"].get(p["id"], p))
                cur.update(builtin=True, modified=p["id"] in self.data["overrides"])
                out.append(cur)
            for p in self.data["custom"]:
                out.append(dict(copy.deepcopy(p), builtin=False, modified=False))
            return out

    def get(self, pid):
        return next((p for p in self.all() if p["id"] == pid), None)

    @property
    def active_id(self):
        return self.data["active"]

    def active(self):
        return self.get(self.data["active"]) or self.get("off")

    def set_active(self, pid):
        if not self.get(pid):
            raise KeyError(pid)
        with self.lock:
            self.data["active"] = pid
            self._save()

    def update(self, pid, preset):
        preset = {k: v for k, v in preset.items() if k not in ("builtin", "modified")}
        preset["id"] = pid
        with self.lock:
            if pid in BUILTIN_IDS:
                self.data["overrides"][pid] = preset
            else:
                for i, p in enumerate(self.data["custom"]):
                    if p["id"] == pid:
                        self.data["custom"][i] = preset
                        break
                else:
                    raise KeyError(pid)
            self._save()

    def duplicate(self, pid):
        src = self.get(pid)
        if not src:
            raise KeyError(pid)
        new = {k: v for k, v in src.items() if k not in ("builtin", "modified")}
        new["name"] = src["name"] + " Copy"
        base = re.sub(r"[^a-z0-9]+", "-", new["name"].lower()).strip("-")
        ids = {p["id"] for p in self.all()}
        new["id"], n = base, 2
        while new["id"] in ids:
            new["id"], n = f"{base}-{n}", n + 1
        with self.lock:
            self.data["custom"].append(new)
            self._save()
        return self.get(new["id"])

    def delete(self, pid):
        with self.lock:
            if pid in BUILTIN_IDS:
                raise ValueError("built-in presets can't be deleted")
            self.data["custom"] = [p for p in self.data["custom"] if p["id"] != pid]
            if self.data["active"] == pid:
                self.data["active"] = "off"
            self._save()

    def reset(self, pid):
        with self.lock:
            self.data["overrides"].pop(pid, None)
            self._save()
