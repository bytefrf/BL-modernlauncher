#!/usr/bin/env bash
# Собирает macOS-бандл .app и образ .dmg.
#
# Подпись ad-hoc (codesign -s -) — она бесплатна и не требует Apple Developer Program.
# Предупреждение Gatekeeper она не убирает (для этого нужна нотаризация, 99 $/год),
# но убирает враньё «приложение повреждено» и делает поведение предсказуемым:
# игрок открывает через Системные настройки → Конфиденциальность → «Открыть всё равно».
#
# Использование: build-macos-package.sh <папка-publish> <версия> [папка-результата]
set -euo pipefail

PUBLISH_DIR="${1:?укажи папку с результатом dotnet publish}"
VERSION="${2:?укажи версию, например 1.3.2}"
OUT_DIR="${3:-packages}"

APP_NAME="BL-modern"
EXE_NAME="Launcher.Avalonia"
BUNDLE_ID="ru.blmodern.launcher"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$OUT_DIR"

APP="$WORK/$APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -r "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

# Иконка: .icns собирается из PNG штатным iconutil.
ICON_SOURCE="$(dirname "$0")/../Launcher.Avalonia/Assets/Sprite-0001.png"
ICONSET="$WORK/icon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$ICON_SOURCE" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null 2>&1 || true
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" >/dev/null 2>&1 || true

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>$EXE_NAME</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <!-- Приложение с окном, а не фоновая служба: без этого нет значка в Dock и меню. -->
    <key>LSUIElement</key><false/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

# Подписываем изнутри наружу: сначала вложенные библиотеки, затем сам бандл.
find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" \) -print0 \
  | xargs -0 -I{} codesign --force --timestamp=none --sign - {} 2>/dev/null || true
codesign --force --deep --timestamp=none --sign - "$APP" 2>/dev/null \
  || echo "codesign недоступен — бандл остаётся неподписанным" >&2

codesign --verify --verbose=1 "$APP" 2>&1 | head -3 || true

DMG_DIR="$WORK/dmg"
mkdir -p "$DMG_DIR"
cp -r "$APP" "$DMG_DIR/"
# Ссылка на /Applications: без неё игрок не понимает, куда перетаскивать.
ln -s /Applications "$DMG_DIR/Applications"

cat > "$DMG_DIR/ЧИТАТЬ.txt" <<'EOF'
Установка:
1. Перетащи BL-modern в папку Applications.
2. Первый запуск: если macOS скажет, что разработчик не проверен, открой
   Системные настройки → Конфиденциальность и безопасность → «Открыть всё равно».
   Либо выполни в Терминале:
       xattr -dr com.apple.quarantine /Applications/BL-modern.app

Приложение не подписано сертификатом Apple: это не вирус, а отсутствие
платного участия в Apple Developer Program.
EOF

DMG_OUT="$OUT_DIR/${APP_NAME}-${VERSION}-arm64.dmg"
rm -f "$DMG_OUT"
hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_DIR" -ov -format UDZO "$DMG_OUT" >/dev/null

echo "APP: $APP_NAME.app"
echo "DMG: $DMG_OUT"
