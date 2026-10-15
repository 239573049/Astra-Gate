#!/usr/bin/env bash
# Astra installer for macOS and Linux.
#
#   curl -fsSL https://astra-gate.si/install.sh | bash
#   curl -fsSL https://astra-gate.si/install.sh | bash -s -- --server
#   curl -fsSL https://astra-gate.si/install.sh | bash -s -- --desktop
#   curl -fsSL https://astra-gate.si/install.sh | bash -s -- --server --preview
#
# Installs either
#   server   the self-contained astra-server binary (+ web UI) into ~/.astra/server and an
#            `astra-server` launcher into ~/.local/bin. No Node.js or npm needed.
#   desktop  the desktop app (macOS .dmg -> /Applications, Linux AppImage -> ~/.local/bin).
#            It bundles its own server.
#
# Without a flag it asks (when a terminal is available) and defaults to the server.
# --preview installs the newest preview release (or a newer stable, should one exist) instead of the
# newest stable; to keep receiving previews afterwards, turn on Settings -> About -> preview updates.
#
# Environment:
#   ASTRA_INSTALL=server|desktop   same as --server / --desktop
#   ASTRA_CHANNEL=stable|preview   release channel (default stable; --preview = preview)
#   ASTRA_HOME                     data directory (default ~/.astra)
#   ASTRA_BIN_DIR                  launcher / AppImage directory (default ~/.local/bin)
#   ASTRA_FEED_URL                 release feed (default https://astra-gate.si/api/client-releases)
#   ASTRA_NPM_REGISTRY             where the server tarball comes from (default https://registry.npmjs.org)
#
# Every download is verified against a sha256 published in the release feed before it is installed.
set -euo pipefail

FEED="${ASTRA_FEED_URL:-https://astra-gate.si/api/client-releases}"
FEED="${FEED%/}"
REGISTRY="${ASTRA_NPM_REGISTRY:-https://registry.npmjs.org}"
REGISTRY="${REGISTRY%/}"
CHANNEL="${ASTRA_CHANNEL:-stable}"
HOME_DIR="${ASTRA_HOME:-$HOME/.astra}"
BIN_DIR="${ASTRA_BIN_DIR:-$HOME/.local/bin}"
MODE="${ASTRA_INSTALL:-}"

say() { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

usage() {
  say "Usage: curl -fsSL https://astra-gate.si/install.sh | bash -s -- [--server | --desktop] [--preview]"
  say "Environment: ASTRA_INSTALL, ASTRA_CHANNEL, ASTRA_HOME, ASTRA_BIN_DIR, ASTRA_FEED_URL, ASTRA_NPM_REGISTRY"
}

while [ $# -gt 0 ]; do
  case "$1" in
    --server) MODE=server ;;
    --desktop) MODE=desktop ;;
    --preview) CHANNEL=preview ;;
    -y|--yes) ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown option '$1' (use --server or --desktop)" ;;
  esac
  shift
done
case "$CHANNEL" in
  stable|preview) ;;
  *) die "unknown release channel '$CHANNEL' (use stable or preview)." ;;
esac

command -v curl >/dev/null 2>&1 || die "curl is required."
command -v tar >/dev/null 2>&1 || die "tar is required."

case "$(uname -s)" in
  Darwin) OS=darwin ;;
  Linux) OS=linux ;;
  *) die "unsupported OS '$(uname -s)'. On Windows use install.ps1." ;;
esac
case "$(uname -m)" in
  arm64|aarch64) ARCH=arm64 ;;
  x86_64|amd64) ARCH=x64 ;;
  *) die "unsupported CPU '$(uname -m)'." ;;
esac
PLATFORM="$OS-$ARCH"
# The manifest keys its platforms by .NET RID.
case "$PLATFORM" in
  darwin-*) RID="osx-$ARCH" ;;
  *) RID="$PLATFORM" ;;
esac
if [ "$OS" = linux ] && ldd --version 2>&1 | grep -qi musl; then
  die "musl-based Linux (e.g. Alpine) is not supported; the server binary needs glibc."
fi

choose_mode() {
  [ -n "$MODE" ] && return
  if [ -r /dev/tty ] && [ -w /dev/tty ]; then
    {
      say ""
      say "What do you want to install?"
      say "  1) Server   - astra-server binary + web console (no Node.js needed)"
      say "  2) Desktop  - the desktop app (includes the server)"
      printf 'Choose [1/2] (default 1): '
    } >/dev/tty
    local answer=""
    read -r answer </dev/tty || true
    case "$answer" in
      2|d|D|desktop) MODE=desktop ;;
      ""|1|s|S|server) MODE=server ;;
      *) die "invalid choice '$answer'." ;;
    esac
  else
    MODE=server
    say "No terminal to ask on; installing the server (pass --desktop for the app)."
  fi
}

# --- helpers ----------------------------------------------------------------

TMP="$(mktemp -d "${TMPDIR:-/tmp}/astra-install.XXXXXX")"
MOUNT=""
cleanup() {
  if [ -n "$MOUNT" ]; then hdiutil detach "$MOUNT" -quiet 2>/dev/null || true; fi
  rm -rf "$TMP"
}
trap cleanup EXIT

fetch() { # url dest
  local progress=(-sS)
  [ -t 2 ] && progress=(--progress-bar)
  curl -fL --retry 3 --connect-timeout 15 "${progress[@]}" -o "$2" "$1" \
    || die "download failed: $1"
}

fetch_text() { curl -fsSL --retry 3 --connect-timeout 15 "$1" | tr -d '\n\r' || die "download failed: $1"; }

# jstr <key>: first string value of "key" in the JSON on stdin (the feed contract is ours, so a
# flat scan is enough and keeps jq / python out of the requirements).
jstr() { { grep -o "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" || true; } | head -1 | sed 's/^[^:]*:[[:space:]]*"\(.*\)"$/\1/'; }

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
  else die "sha256sum or shasum is required to verify downloads."; fi
}

verify() { # file expected-sha256
  local actual
  actual="$(sha256_of "$1")"
  [ "$(printf '%s' "$actual" | tr 'A-F' 'a-f')" = "$(printf '%s' "$2" | tr 'A-F' 'a-f')" ] \
    || die "checksum mismatch for $(basename "$1") (expected $2, got $actual). Nothing was installed."
}

latest_version() {
  local json version
  json="$(fetch_text "$FEED/$CHANNEL/latest.json")"
  version="$(printf '%s' "$json" | jstr version)"
  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]] \
    || die "the release feed returned no usable version."
  LATEST_JSON="$json"
  VERSION="$version"
}

# --- server -----------------------------------------------------------------

install_server() {
  local version entry package sha tgz dir bin_name="astra-server" server_dir="$HOME_DIR/server"
  latest_version
  version="$VERSION"
  entry="$(printf '%s' "$LATEST_JSON" | grep -o "\"$RID\"[[:space:]]*:[[:space:]]*{[^}]*}" | head -1 || true)"
  [ -n "$entry" ] || die "no server build for $PLATFORM in the $version release."
  package="$(printf '%s' "$entry" | jstr server)"
  sha="$(printf '%s' "$entry" | jstr serverSha256)"
  [[ "$package" =~ ^@aidotnet/server-[a-z0-9-]+$ ]] || die "the release feed named an unexpected package '$package'."
  [ -n "$sha" ] || die "the release feed has no checksum for the $PLATFORM server."

  say "Installing Astra server $version ($PLATFORM)…"
  tgz="$TMP/server.tgz"
  fetch "$REGISTRY/$package/-/${package#@aidotnet/}-$version.tgz" "$tgz"
  dir="$TMP/pkg"
  mkdir -p "$dir"
  tar -xzf "$tgz" -C "$dir"
  [ -f "$dir/package/bin/$bin_name" ] || die "the downloaded package has no server binary."
  say "Verifying checksum…"
  verify "$dir/package/bin/$bin_name" "$sha"

  mkdir -p "$server_dir" "$BIN_DIR"
  local target="$server_dir/$bin_name-$version"
  cp "$dir/package/bin/$bin_name" "$target.tmp"
  chmod 755 "$target.tmp"
  # The server loads SQLite and serves the UI from the directory of its own executable.
  local lib
  for lib in "$dir"/package/bin/*.dylib "$dir"/package/bin/*.so "$dir"/package/bin/*.so.*; do
    [ -f "$lib" ] || continue
    cp "$lib" "$server_dir/$(basename "$lib").tmp"
    mv -f "$server_dir/$(basename "$lib").tmp" "$server_dir/$(basename "$lib")"
  done
  if [ -d "$dir/package/bin/wwwroot" ]; then
    rm -rf "$server_dir/wwwroot.new"
    cp -R "$dir/package/bin/wwwroot" "$server_dir/wwwroot.new"
    rm -rf "$server_dir/wwwroot"
    mv "$server_dir/wwwroot.new" "$server_dir/wwwroot"
  else
    warn "the package ships no web UI; the console will not be served."
  fi
  mv -f "$target.tmp" "$target"

  write_install_json "$target" "$version"
  # Older managed binaries are dead weight once install.json points at the new one.
  local old
  for old in "$server_dir/$bin_name"-*; do
    [ -f "$old" ] && [ "$old" != "$target" ] && rm -f "$old"
  done

  # A launcher instead of a symlink: it follows install.json, so a later `astra update` or
  # desktop update (which writes a new versioned binary) keeps working.
  cat >"$BIN_DIR/astra-server" <<EOF
#!/bin/sh
home="\${ASTRA_HOME:-\$HOME/.astra}"
bin=\$(sed -n 's/.*"serverPath"[[:space:]]*:[[:space:]]*"\\([^"]*\\)".*/\\1/p' "\$home/install.json" 2>/dev/null | head -1)
[ -x "\$bin" ] || bin="$target"
exec "\$bin" "\$@"
EOF
  chmod 755 "$BIN_DIR/astra-server"

  say ""
  say "Astra server $version installed."
  say "  binary    $target"
  say "  launcher  $BIN_DIR/astra-server"
  case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) say ""; say "Add $BIN_DIR to your PATH:  export PATH=\"$BIN_DIR:\$PATH\"" ;;
  esac
  say ""
  say "Start it with:  astra-server serve"
  say "Then open the URL it prints (default http://127.0.0.1:<port>)."
  say "To update later, run this installer again."
  if [ -f "$HOME_DIR/runtime.json" ]; then
    say ""
    say "If Astra is already running, restart it to use the new version."
  fi
}

# install.json is shared with the CLI and desktop app: keep the fields we do not own.
write_install_json() { # serverPath version
  local file="$HOME_DIR/install.json" dpath="" dver="" extra=""
  if [ -f "$file" ]; then
    local flat
    flat="$(tr -d '\n\r' <"$file")"
    dpath="$(printf '%s' "$flat" | jstr desktopPath)"
    dver="$(printf '%s' "$flat" | jstr desktopVersion)"
  fi
  [ -n "$dpath" ] && extra="$extra  \"desktopPath\": \"$dpath\",
"
  [ -n "$dver" ] && extra="$extra  \"desktopVersion\": \"$dver\",
"
  mkdir -p "$HOME_DIR"
  printf '{\n  "serverPath": "%s",\n  "serverVersion": "%s",\n%s  "updatedAt": "%s"\n}\n' \
    "$1" "$2" "$extra" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$file.tmp"
  mv -f "$file.tmp" "$file"
}

# --- desktop ----------------------------------------------------------------

install_desktop() {
  local version ext name file releases entry sha
  latest_version
  version="$VERSION"
  case "$OS" in
    darwin) ext=dmg ;;
    linux) ext=AppImage ;;
  esac
  name="Astra-$version-$PLATFORM.$ext"

  say "Installing Astra desktop $version ($PLATFORM)…"
  releases="$(fetch_text "$FEED/$CHANNEL/releases.json")"
  entry="$(printf '%s' "$releases" | grep -o "{[^{}]*\"name\"[[:space:]]*:[[:space:]]*\"$name\"[^{}]*}" | head -1 || true)"
  [ -n "$entry" ] || die "the $version release has no desktop build for $PLATFORM ($name)."
  sha="$(printf '%s' "$entry" | jstr sha256)"
  [ -n "$sha" ] || die "the release feed has no checksum for $name."

  file="$TMP/$name"
  fetch "$FEED/$CHANNEL/v/$version/$name" "$file"
  say "Verifying checksum…"
  verify "$file" "$sha"

  if [ "$OS" = darwin ]; then
    install_dmg "$file"
  else
    install_appimage "$file"
  fi
}

install_dmg() {
  local dmg="$1" app dest
  MOUNT="$TMP/mnt"
  mkdir -p "$MOUNT"
  hdiutil attach -nobrowse -readonly -noverify -mountpoint "$MOUNT" "$dmg" -quiet \
    || die "could not mount the disk image."
  app="$(ls -d "$MOUNT"/*.app 2>/dev/null | head -1 || true)"
  [ -n "$app" ] || die "the disk image contains no application."
  if [ -w /Applications ]; then dest=/Applications; else dest="$HOME/Applications"; mkdir -p "$dest"; fi
  if pgrep -xq "$(basename "$app" .app)" 2>/dev/null; then
    warn "$(basename "$app" .app) is running; quit it first so the new version can replace it."
  fi
  rm -rf "$dest/$(basename "$app")"
  ditto "$app" "$dest/$(basename "$app")"
  say ""
  say "Installed $dest/$(basename "$app")."
  say "Open it from Launchpad, or run:  open -a \"$(basename "$app" .app)\""
}

install_appimage() {
  local file="$1" dest="$BIN_DIR/Astra.AppImage"
  mkdir -p "$BIN_DIR"
  cp "$file" "$dest.tmp"
  chmod 755 "$dest.tmp"
  mv -f "$dest.tmp" "$dest"
  say ""
  say "Installed $dest."
  if ! command -v fusermount >/dev/null 2>&1 && ! command -v fusermount3 >/dev/null 2>&1; then
    warn "FUSE is not installed, so AppImages cannot mount themselves. Install libfuse2 (Debian/Ubuntu: sudo apt install libfuse2),"
    warn "or run:  $dest --appimage-extract-and-run"
  fi
  say "Start it with:  $dest"
  case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) say "(add $BIN_DIR to your PATH to start it by name)" ;;
  esac
}

# --- main -------------------------------------------------------------------

choose_mode
case "$MODE" in
  server) install_server ;;
  desktop) install_desktop ;;
  *) die "unknown install target '$MODE' (use server or desktop)." ;;
esac
