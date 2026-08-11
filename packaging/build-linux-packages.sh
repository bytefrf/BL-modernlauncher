#!/usr/bin/env bash
# Собирает пакеты лаунчера для Linux: .deb и .AppImage.
#
# Зачем два формата: .deb ставится в систему и даёт ярлык в меню, .AppImage не требует
# установки вообще (скачал, chmod +x, запустил) — для игрока это самый короткий путь
# и единственный вариант на не-Debian системах.
#
# Использование: build-linux-packages.sh <папка-publish> <версия> [папка-результата]
set -euo pipefail

PUBLISH_DIR="${1:?укажи папку с результатом dotnet publish}"
VERSION="${2:?укажи версию, например 1.3.2}"
OUT_DIR="${3:-packages}"

APP_ID="bl-modern"
APP_NAME="BL-modern"
EXE_NAME="Launcher.Avalonia"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$OUT_DIR"

if [[ ! -x "$PUBLISH_DIR/$EXE_NAME" ]]; then
  chmod +x "$PUBLISH_DIR/$EXE_NAME" 2>/dev/null || true
fi

# Иконка: PNG из ресурсов приложения. Без неё .desktop покажет пустой квадрат.
ICON_SOURCE="$(dirname "$0")/../Launcher.Avalonia/Assets/Sprite-0001.png"

write_desktop_entry() {
  local target="$1" exec_line="$2"
  cat > "$target" <<EOF
[Desktop Entry]
Type=Application
Name=$APP_NAME
Comment=Лаунчер сборок Minecraft BL-modern
Exec=$exec_line
Icon=$APP_ID
Terminal=false
Categories=Game;
StartupWMClass=$EXE_NAME
EOF
}

# ── .deb ──────────────────────────────────────────────────────────────────────
# Приложение целиком кладём в /opt: оно самодостаточное (.NET внутри) и не должно
# растекаться по /usr/lib.
DEB_ROOT="$WORK/deb"
mkdir -p "$DEB_ROOT/DEBIAN" \
         "$DEB_ROOT/opt/$APP_ID" \
         "$DEB_ROOT/usr/bin" \
         "$DEB_ROOT/usr/share/applications" \
         "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUBLISH_DIR/." "$DEB_ROOT/opt/$APP_ID/"
chmod +x "$DEB_ROOT/opt/$APP_ID/$EXE_NAME"
cp "$ICON_SOURCE" "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
ln -s "/opt/$APP_ID/$EXE_NAME" "$DEB_ROOT/usr/bin/$APP_ID"
write_desktop_entry "$DEB_ROOT/usr/share/applications/$APP_ID.desktop" "/opt/$APP_ID/$EXE_NAME"

# Зависимости минимальны: ICU лежит внутри пакета, .NET — тоже. Остаются только
# библиотеки, которые нужны Avalonia для окна и шрифтов.
cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: $APP_ID
Version: $VERSION
Section: games
Priority: optional
Architecture: amd64
Depends: libx11-6, libice6, libsm6, libfontconfig1, libfreetype6
Recommends: libgl1
Maintainer: BL-modern <support@bl-modern.ru>
Description: Лаунчер сборок Minecraft BL-modern
 Ставит и обновляет сборки, сам качает нужную Java и запускает игру.
EOF

dpkg-deb --build --root-owner-group "$DEB_ROOT" "$OUT_DIR/${APP_ID}_${VERSION}_amd64.deb" >/dev/null
echo "DEB: $OUT_DIR/${APP_ID}_${VERSION}_amd64.deb"

# ── .AppImage ─────────────────────────────────────────────────────────────────
APPDIR="$WORK/AppDir"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -r "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$EXE_NAME"
cp "$ICON_SOURCE" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
cp "$ICON_SOURCE" "$APPDIR/$APP_ID.png"
write_desktop_entry "$APPDIR/$APP_ID.desktop" "$EXE_NAME"

# AppRun обязан быть исполняемым скриптом в корне AppDir — это точка входа.
cat > "$APPDIR/AppRun" <<EOF
#!/bin/sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
exec "\$HERE/usr/bin/$EXE_NAME" "\$@"
EOF
chmod +x "$APPDIR/AppRun"

APPIMAGETOOL="$WORK/appimagetool"
if ! curl -sSL -o "$APPIMAGETOOL" \
    "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"; then
  echo "AppImage: не удалось скачать appimagetool — пропускаю" >&2
  exit 0
fi
chmod +x "$APPIMAGETOOL"

# На раннере нет FUSE, поэтому распаковываем инструмент и зовём его напрямую.
( cd "$WORK" && ./appimagetool --appimage-extract >/dev/null )
APPIMAGE_OUT="$OUT_DIR/${APP_NAME}-${VERSION}-x86_64.AppImage"
ARCH=x86_64 "$WORK/squashfs-root/AppRun" "$APPDIR" "$APPIMAGE_OUT" >/dev/null 2>&1 || {
  echo "AppImage: сборка не удалась" >&2
  exit 0
}

chmod +x "$APPIMAGE_OUT"
echo "APPIMAGE: $APPIMAGE_OUT"
