#!/usr/bin/env bash
# Controller Studio Pro installer.
#   curl -fsSL https://raw.githubusercontent.com/nomad9021/Controller-Stuidio-Pro/main/install.sh | bash
# Uninstall:
#   curl -fsSL https://raw.githubusercontent.com/nomad9021/Controller-Stuidio-Pro/main/install.sh | bash -s -- --uninstall
set -euo pipefail

REPO="https://github.com/nomad9021/Controller-Stuidio-Pro.git"
APP_ID="controller-studio-pro"
APP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$APP_ID"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/scalable/apps"
BIN_DIR="$HOME/.local/bin"

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m  %s\n' "$*" >&2; }
die()  { printf '\033[1;31mxx\033[0m  %s\n' "$*" >&2; exit 1; }

# sudo that still works when this script is piped into bash (asks on the terminal).
as_root() {
  if [ "$(id -u)" -eq 0 ]; then "$@"
  elif sudo -n true 2>/dev/null; then sudo "$@"
  elif [ -r /dev/tty ] && { true </dev/tty; } 2>/dev/null; then sudo "$@" </dev/tty
  else return 1
  fi
}

uninstall() {
  say "Removing Controller Studio Pro"
  systemctl --user disable --now "$APP_ID.service" 2>/dev/null || true
  rm -f "$UNIT_DIR/$APP_ID.service" "$DESKTOP_DIR/$APP_ID.desktop" "$ICON_DIR/$APP_ID.svg" "$BIN_DIR/$APP_ID"
  systemctl --user daemon-reload 2>/dev/null || true
  flatpak override --user --unset-env=SDL_GAMECONTROLLER_IGNORE_DEVICES --unset-env=SDL_JOYSTICK_HIDAPI_PS5 \
    com.moonlight_stream.Moonlight 2>/dev/null || true
  rm -rf "$APP_DIR"
  if as_root rm -f /etc/udev/rules.d/70-dualsense-hidraw.rules /etc/udev/rules.d/71-controller-studio-uinput.rules; then
    as_root udevadm control --reload || true
  else
    warn "Couldn't remove the udev rules in /etc/udev/rules.d (needs sudo)."
  fi
  say "Done. Your presets are still in ~/.config/$APP_ID (delete it to remove them too)."
}

install_deps() {
  local missing=0
  python3 -c 'import gi; gi.require_version("Gtk","3.0"); gi.require_version("WebKit2","4.1"); from gi.repository import Gtk, WebKit2' \
    2>/dev/null || missing=1
  command -v xprop >/dev/null || missing=1
  command -v git >/dev/null || missing=1
  [ "$missing" -eq 0 ] && { say "Dependencies already installed"; return; }

  say "Installing dependencies (Python GTK, WebKitGTK, xprop, git)"
  if command -v dnf >/dev/null; then
    as_root dnf install -y python3 python3-gobject gtk3 webkit2gtk4.1 xprop git
  elif command -v apt-get >/dev/null; then
    as_root apt-get update && as_root apt-get install -y python3 python3-gi gir1.2-gtk-3.0 gir1.2-webkit2-4.1 x11-utils git
  elif command -v pacman >/dev/null; then
    as_root pacman -S --needed --noconfirm python python-gobject gtk3 webkit2gtk-4.1 xorg-xprop git
  elif command -v zypper >/dev/null; then
    as_root zypper install -y python3 python3-gobject typelib-1_0-Gtk-3_0 typelib-1_0-WebKit2-4_1 xprop git
  else
    die "Unknown package manager. Install Python 3 GObject, GTK 3, WebKit2GTK 4.1, xprop and git, then run this again."
  fi || die "Installing dependencies failed (sudo is needed)."
}

fetch_app() {
  if [ -d "$APP_DIR/.git" ]; then
    say "Updating $APP_DIR"
    git -C "$APP_DIR" pull --ff-only --quiet
  else
    say "Downloading to $APP_DIR"
    rm -rf "$APP_DIR"
    git clone --depth 1 --quiet "$REPO" "$APP_DIR"
  fi
  chmod +x "$APP_DIR/$APP_ID"
}

install_udev() {
  say "Allowing your user to talk to the controller (udev rules, needs sudo)"
  local changed=0 f
  for f in "$APP_DIR"/udev/*.rules; do
    if ! cmp -s "$f" "/etc/udev/rules.d/$(basename "$f")"; then changed=1; fi
  done
  [ "$changed" -eq 0 ] && { say "udev rules already in place"; return; }
  if as_root install -m 644 "$APP_DIR"/udev/*.rules /etc/udev/rules.d/ \
     && as_root udevadm control --reload && as_root udevadm trigger; then
    say "udev rules installed; turn the controller off and on (hold PS) if it was connected"
  else
    warn "Skipped: couldn't use sudo. Run this yourself later:"
    warn "  sudo cp $APP_DIR/udev/*.rules /etc/udev/rules.d/ && sudo udevadm control --reload && sudo udevadm trigger"
  fi
}

install_desktop() {
  say "Adding the background service and app launcher"
  mkdir -p "$UNIT_DIR" "$DESKTOP_DIR" "$ICON_DIR" "$BIN_DIR"
  cat >"$UNIT_DIR/$APP_ID.service" <<EOF
[Unit]
Description=Controller Studio Pro – DualSense trigger effects, lighting and output
After=bluetooth.target

[Service]
ExecStart=/usr/bin/env python3 -u -m controllerstudio.server
WorkingDirectory=$APP_DIR
Restart=on-failure
RestartSec=3

[Install]
WantedBy=default.target
EOF
  install -m 644 "$APP_DIR/assets/$APP_ID.svg" "$ICON_DIR/$APP_ID.svg"
  cat >"$DESKTOP_DIR/$APP_ID.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Controller Studio Pro
Comment=Adaptive trigger presets, lighting and a controller tester for DualSense
Exec=$APP_DIR/$APP_ID
Icon=$APP_ID
Terminal=false
Categories=Game;Utility;
StartupWMClass=$APP_ID
EOF
  ln -sf "$APP_DIR/$APP_ID" "$BIN_DIR/$APP_ID"
  command -v update-desktop-database >/dev/null && update-desktop-database "$DESKTOP_DIR" 2>/dev/null || true

  # Replace the pre-release "Pad Studio" install if it's here.
  if [ -f "$UNIT_DIR/pad-studio.service" ]; then
    systemctl --user disable --now pad-studio.service 2>/dev/null || true
    rm -f "$UNIT_DIR/pad-studio.service" "$DESKTOP_DIR/pad-studio.desktop"
  fi

  systemctl --user daemon-reload
  systemctl --user enable --now "$APP_ID.service" >/dev/null 2>&1
  systemctl --user restart "$APP_ID.service"
}

main() {
  [ "$(uname -s)" = Linux ] || die "This installer is for Linux. On Windows use install.ps1 (see the README)."
  [ "${1:-}" = "--uninstall" ] && { uninstall; return; }
  install_deps
  fetch_app
  install_udev
  install_desktop
  say "Installed! Open “Controller Studio Pro” from your app menu, or run: $APP_ID"
}

main "$@"
