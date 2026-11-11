#!/usr/bin/env bash
# Собирает macOS-приложение: .app + .dmg (без подписи и нотаризации).
# Использование: packaging/macos/make-dmg.sh <путь-к-publish> <версия> <arch> <куда-положить>
# Пример:       packaging/macos/make-dmg.sh artifacts/osx-arm64 0.1.0 arm64 artifacts/installers
set -euo pipefail

SRC="${1:?укажите папку publish}"
VERSION="${2:?укажите версию}"
ARCH="${3:?укажите архитектуру (arm64|x64)}"
OUT="${4:-artifacts/installers}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_NAME="EHOT Launcher"
BUNDLE="$OUT/$APP_NAME.app"
DMG="$OUT/EHOT-Launcher-${VERSION}-osx-${ARCH}.dmg"

rm -rf "$BUNDLE" "$DMG"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources" "$OUT"

# 1. Бинарники и данные — прямо в Contents/MacOS (там же, где .NET-хост).
cp -R "$SRC"/. "$BUNDLE/Contents/MacOS/"
chmod +x "$BUNDLE/Contents/MacOS/EHOT.Launcher" 2>/dev/null || true

# 2. Иконка и Info.plist (версия подставляется из CI).
cp "$ROOT/src/EHOT.Launcher.UI/Assets/ehot.icns" "$BUNDLE/Contents/Resources/ehot.icns"
sed "s/__VERSION__/${VERSION}/g" "$ROOT/packaging/macos/Info.plist" > "$BUNDLE/Contents/Info.plist"
printf 'APPL????' > "$BUNDLE/Contents/PkgInfo"

# 3. Символы отладки в бандл не кладём.
find "$BUNDLE" -name '*.pdb' -delete

# 4. Проверка, что бандл вообще запускаемый (на Linux/macOS CI это возможно).
if [ ! -x "$BUNDLE/Contents/MacOS/EHOT.Launcher" ]; then
  echo "ОШИБКА: исполняемый файл EHOT.Launcher не найден в $SRC" >&2
  exit 1
fi

# 5. DMG: папка приложения + ярлык /Applications рядом (привычный drag-and-drop).
STAGE="$(mktemp -d)"
cp -R "$BUNDLE" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "EHOT Launcher" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

echo "Готово: $DMG ($(du -h "$DMG" | cut -f1))"
