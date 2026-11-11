#!/usr/bin/env bash
# Подготавливает .NET 10 SDK для локальной сборки в песочнице/CI.
# SDK ставится в кэш-каталог (не попадает в git и в снапшоты рабочего пространства).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK_VERSION="${EHOT_DOTNET_SDK:-10.0.403}"
DOTNET_ROOT="${EHOT_DOTNET_ROOT:-$HOME/.cache/dotnet}"
# В песочницах /tmp часто смонтирован как маленький tmpfs — распаковываем через кэш проекта.
export TMPDIR="${TMPDIR:-$ROOT/.cache/tmp}"
mkdir -p "$TMPDIR"

if [ -x "$DOTNET_ROOT/dotnet" ] && "$DOTNET_ROOT/dotnet" --version >/dev/null 2>&1; then
  export DOTNET_ROOT
  export PATH="$DOTNET_ROOT:$PATH"
  echo "dotnet уже установлен: $("$DOTNET_ROOT/dotnet" --version)"
  exit 0
fi

echo "Устанавливаю .NET SDK $SDK_VERSION в $DOTNET_ROOT …"
mkdir -p "$DOTNET_ROOT"
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version "$SDK_VERSION" --install-dir "$DOTNET_ROOT" --no-path

export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export NUGET_PACKAGES="${NUGET_PACKAGES:-$ROOT/.cache/nuget}"

echo "Готово: $(dotnet --version)"
