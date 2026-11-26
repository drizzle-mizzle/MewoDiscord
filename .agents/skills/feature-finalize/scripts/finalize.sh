#!/usr/bin/env bash
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-dotnet}"
DOCKER="${DOCKER:-docker}"
SOLUTION="src/MewoDiscord.sln"
TEST_FILTER='FullyQualifiedName~Regex_|FullyQualifiedName~Store_|FullyQualifiedName~Telegram_|FullyQualifiedName~X_|FullyQualifiedName~Watcher_|FullyQualifiedName~Gpt_|FullyQualifiedName~Messages_|FullyQualifiedName~Action_|FullyQualifiedName~Mentions_|FullyQualifiedName~Media_|FullyQualifiedName~Events_'

if ! command -v "$DOTNET" >/dev/null 2>&1; then
    echo "dotnet не найден. Укажите полный путь через DOTNET." >&2
    exit 127
fi

if ! command -v "$DOCKER" >/dev/null 2>&1; then
    echo "docker не найден. Укажите полный путь через DOCKER." >&2
    exit 127
fi

printf '\n==> Восстановление NuGet\n'
"$DOTNET" restore "$SOLUTION"

printf '\n==> Форматирование whitespace/style/analyzers\n'
"$DOTNET" format whitespace "$SOLUTION" --no-restore
"$DOTNET" format style "$SOLUTION" --no-restore --severity info
"$DOTNET" format analyzers "$SOLUTION" --no-restore --severity info

printf '\n==> Контроль неизменности форматирования\n'
"$DOTNET" format whitespace "$SOLUTION" --no-restore --verify-no-changes
"$DOTNET" format style "$SOLUTION" --no-restore --severity info --verify-no-changes
"$DOTNET" format analyzers "$SOLUTION" --no-restore --severity info --verify-no-changes

printf '\n==> Release-сборка всего решения\n'
"$DOTNET" build "$SOLUTION" -c Release --no-restore

printf '\n==> Автономные тесты без АИ_*\n'
"$DOTNET" test src/MewoDiscord.Tests/MewoDiscord.Tests.csproj \
    -c Release --no-build --no-restore --filter "$TEST_FILTER"

printf '\n==> Проверка Compose и реальная сборка Docker-образа\n'
"$DOCKER" compose config --quiet
"$DOCKER" compose build mewo-bot

printf '\n==> Проверка whitespace в Git diff\n'
git diff --check

printf '\nФинализация успешно завершена. Проверьте итоговый diff перед коммитом.\n'
