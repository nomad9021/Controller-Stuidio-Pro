"""Controller Studio Pro window on Windows: a frameless WebView2 window (pywebview)
around the local service. The page draws the title bar; this moves, resizes,
maximizes and closes the window when the page asks.

Run with: pythonw -m controllerstudio.winwindow
"""
import ctypes
import os
import subprocess
import sys
import threading
import time
import urllib.request
from ctypes import wintypes

import webview

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = "http://127.0.0.1:%s/" % os.environ.get("CSP_PORT", "8765")
TITLE = "Controller Studio Pro"
MIN_W, MIN_H = 900, 600
LOG = os.path.join(os.environ.get("LOCALAPPDATA", APP_DIR), "controller-studio-pro", "server.log")

user32 = ctypes.windll.user32
dwmapi = ctypes.windll.dwmapi
SWP_NOSIZE, SWP_NOZORDER, SWP_NOACTIVATE = 0x0001, 0x0004, 0x0010

# Prototypes, so 64-bit handles aren't squeezed into C ints.
for name, res, args in (
    ("GetWindowRect", wintypes.BOOL, (wintypes.HWND, ctypes.POINTER(wintypes.RECT))),
    ("SetWindowPos", wintypes.BOOL, (wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                                     ctypes.c_int, ctypes.c_int, wintypes.UINT)),
    ("MonitorFromWindow", wintypes.HMONITOR, (wintypes.HWND, wintypes.DWORD)),
    ("GetMonitorInfoW", wintypes.BOOL, (wintypes.HMONITOR, ctypes.c_void_p)),
    ("FindWindowW", wintypes.HWND, (wintypes.LPCWSTR, wintypes.LPCWSTR)),
    ("LoadImageW", wintypes.HANDLE, (wintypes.HINSTANCE, wintypes.LPCWSTR, wintypes.UINT,
                                     ctypes.c_int, ctypes.c_int, wintypes.UINT)),
    ("SendMessageW", wintypes.LPARAM, (wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)),
    ("MessageBoxW", ctypes.c_int, (wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.UINT)),
):
    fn = getattr(user32, name)
    fn.restype, fn.argtypes = res, args


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]


def service_up():
    try:
        urllib.request.urlopen(URL + "api/status", timeout=0.5).read()
        return True
    except OSError:
        return False


def ensure_service():
    """Start the background service if it isn't running. Returns False if it won't start."""
    if service_up():
        return True
    pythonw = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    with open(LOG, "w", encoding="utf-8") as log:  # pythonw has no console, so keep its errors here
        proc = subprocess.Popen(
            [pythonw if os.path.exists(pythonw) else sys.executable, "-m", "controllerstudio.server"],
            cwd=APP_DIR, creationflags=0x00000008 | 0x00000200 | 0x08000000,  # detached, own group, no console
            stdin=subprocess.DEVNULL, stdout=log, stderr=log)
    for _ in range(100):
        if service_up():
            return True
        if proc.poll() is not None:
            break
        time.sleep(0.1)
    return service_up()


def service_failed():
    try:
        with open(LOG, encoding="utf-8", errors="replace") as f:
            detail = f.read()[-1500:].strip()
    except OSError:
        detail = ""
    user32.MessageBoxW(None, "The Controller Studio Pro background service didn't start.\n\n"
                       + (detail or "It exited without an error message.") + f"\n\nLog: {LOG}",
                       TITLE, 0x10)  # MB_ICONERROR


def system_flag(action):
    """A boolean from SystemParametersInfo."""
    val = wintypes.BOOL()
    user32.SystemParametersInfoW(action, 0, ctypes.byref(val), 0)
    return bool(val.value)


def high_contrast():
    class HIGHCONTRAST(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.UINT), ("dwFlags", wintypes.DWORD), ("lpszDefaultScheme", wintypes.LPWSTR)]
    hc = HIGHCONTRAST(ctypes.sizeof(HIGHCONTRAST), 0, None)
    user32.SystemParametersInfoW(0x0042, hc.cbSize, ctypes.byref(hc), 0)  # SPI_GETHIGHCONTRAST
    return bool(hc.dwFlags & 1)


def light_theme():
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as key:
            return bool(winreg.QueryValueEx(key, "AppsUseLightTheme")[0])
    except OSError:
        return True


class Host:
    """Called by the page through window.pywebview.api. pywebview exposes every public
    attribute to the page, so everything but win() starts with an underscore."""

    def __init__(self):
        self._window = None
        self._hwnd = None
        self._maximized = False
        self._normal_rect = None
        self._tracking = False

    # ---- helpers (not exposed: pywebview skips names starting with _)

    def _rect(self):
        r = wintypes.RECT()
        user32.GetWindowRect(self._hwnd, ctypes.byref(r))
        return r.left, r.top, r.right, r.bottom

    def _cursor(self):
        p = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(p))
        return p.x, p.y

    def _work_area(self):
        mi = MONITORINFO(ctypes.sizeof(MONITORINFO))
        user32.GetMonitorInfoW(user32.MonitorFromWindow(self._hwnd, 2), ctypes.byref(mi))
        w = mi.rcWork
        return w.left, w.top, w.right, w.bottom

    def _place(self, l, t, r, b, flags=0):
        user32.SetWindowPos(self._hwnd, None, l, t, r - l, b - t, SWP_NOZORDER | SWP_NOACTIVATE | flags)

    def _set_maximized(self, on):
        if on == self._maximized:
            return
        if on:
            self._normal_rect = self._rect()
            self._place(*self._work_area())  # not full screen: keep the taskbar visible
        elif self._normal_rect:
            self._place(*self._normal_rect)
        self._maximized = on
        self._window.evaluate_js(f"document.documentElement.classList.toggle('maximized', {str(on).lower()})")

    def _track(self, edge):
        """Follow the mouse until the button is released, moving or resizing the window."""
        if self._tracking:
            return
        self._tracking = True
        try:
            sx, sy = self._cursor()
            if edge is None and self._maximized:
                # Dragging a maximized window restores it under the pointer, like Windows does.
                l, t, r, b = self._rect()
                frac = (sx - l) / max(1, r - l)
                self._set_maximized(False)
                nl, nt, nr, nb = self._rect()
                w = nr - nl
                self._place(sx - int(w * frac), sy - 16, sx - int(w * frac) + w, sy - 16 + (nb - nt))
            l, t, r, b = self._rect()
            while user32.GetAsyncKeyState(0x01) & 0x8000:  # VK_LBUTTON
                x, y = self._cursor()
                dx, dy = x - sx, y - sy
                if edge is None:
                    self._place(l + dx, t + dy, r + dx, b + dy, SWP_NOSIZE)
                else:
                    nl, nt, nr, nb = l, t, r, b
                    if "w" in edge:
                        nl = min(l + dx, r - MIN_W)
                    if "e" in edge:
                        nr = max(r + dx, l + MIN_W)
                    if "n" in edge:
                        nt = min(t + dy, b - MIN_H)
                    if "s" in edge:
                        nb = max(b + dy, t + MIN_H)
                    self._place(nl, nt, nr, nb)
                time.sleep(1 / 120)
        finally:
            self._tracking = False

    # ---- called from the page

    def win(self, msg):
        if not self._hwnd:
            return
        if msg == "drag":
            threading.Thread(target=self._track, args=(None,), daemon=True).start()
        elif msg.startswith("resize:") and not self._maximized:
            threading.Thread(target=self._track, args=(msg[7:],), daemon=True).start()
        elif msg == "minimize":
            self._window.minimize()
        elif msg == "maximize":
            self._set_maximized(not self._maximized)
        elif msg == "close":
            self._window.destroy()


def on_shown(host):
    try:
        host._hwnd = host._window.native.Handle.ToInt64()
    except Exception:
        host._hwnd = user32.FindWindowW(None, TITLE)
    hwnd = host._hwnd
    corner = ctypes.c_int(2)  # DWMWCP_ROUND: Windows 11 rounds the frameless window
    dwmapi.DwmSetWindowAttribute(wintypes.HWND(hwnd), 33, ctypes.byref(corner), 4)
    dark = ctypes.c_int(0 if light_theme() else 1)
    dwmapi.DwmSetWindowAttribute(wintypes.HWND(hwnd), 20, ctypes.byref(dark), 4)  # DWMWA_USE_IMMERSIVE_DARK_MODE
    ico = os.path.join(APP_DIR, "assets", "controller-studio-pro.ico")
    if os.path.exists(ico):
        for size, which in ((16, 0), (32, 1)):  # ICON_SMALL, ICON_BIG
            h = user32.LoadImageW(None, ico, 1, size, size, 0x10)  # IMAGE_ICON, LR_LOADFROMFILE
            if h:
                user32.SendMessageW(hwnd, 0x0080, which, h)  # WM_SETICON


def main():
    try:  # own taskbar button and icon instead of Python's
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("nomad9021.ControllerStudioPro")
    except (AttributeError, OSError):
        pass
    if not ensure_service():
        service_failed()
        return
    query = {
        "os": "win",
        "hc": "1" if high_contrast() else "0",
        "rm": "0" if system_flag(0x1042) else "1",  # SPI_GETCLIENTAREAANIMATION
    }
    host = Host()
    host._window = webview.create_window(
        TITLE, URL + "?" + "&".join(f"{k}={v}" for k, v in query.items()),
        js_api=host, width=1240, height=820, min_size=(MIN_W, MIN_H),
        frameless=True, easy_drag=False, shadow=True,
        background_color="#f2f2f7" if light_theme() else "#1c1c1e")
    host._window.events.shown += lambda: on_shown(host)
    webview.start(private_mode=False, storage_path=os.path.join(
        os.environ.get("LOCALAPPDATA", APP_DIR), "controller-studio-pro", "webview"),
        debug="--dev" in sys.argv)


if __name__ == "__main__":
    main()
