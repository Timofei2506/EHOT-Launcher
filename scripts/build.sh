#!/usr/bin/env bash
# EHOT Launcher — сборка и проверки.
# Использование:
#   scripts/build.sh            — restore + build solution
#   scripts/build.sh test       — build + тесты
#   scripts/build.sh publish win|mac-arm|mac-x64
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash "$ROOT/scripts/ensure-sdk.sh" >/dev/null

export DOTNET_ROOT="${EHOT_DOTNET_ROOT:-$HOME/.cache/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget}"

CMD="${1:-build}"

case "$CMD" in
  test)
    dotnet test "$ROOT/EHOT.Launcher.slnx" -c Debug --logger "console;verbosity=normal"
    ;;
  publish)
    case "${2:-win}" in
      win)     RID=win-x64 ;;
      mac-arm) RID=osx-arm64 ;;
      mac-x64) RID=osx-x64 ;;
      linux)   RID=linux-x64 ;;
      *) echo "Неизвестная платформа: $2" >&2; exit 2 ;;
    esac
    OUT="$ROOT/artifacts/$RID"
    rm -rf "$OUT"
    dotnet publish "$ROOT/src/EHOT.Launcher.UI/EHOT.Launcher.UI.csproj" \
      -c Release -r "$RID" --self-contained true \
      -p:DebugType=embedded -o "$OUT"
    echo "Готово: $OUT"
    ;;
  api)
    dotnet run --project "$ROOT/src/EHOT.Launcher.API/EHOT.Launcher.API.csproj"
    ;;
  *)
    dotnet build "$ROOT/EHOT.Launcher.slnx" -c Debug -v minimal
    ;;
esac
