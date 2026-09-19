#!/usr/bin/env sh
# Ботом управляет внешний MCP-клиент (Claude Code, редактор): он водит бота
# по миру своими руками и видит настоящий результат. Удобно гонять моды.
#
# ВНИМАНИЕ: в этом режиме stdout занят протоколом — журнал пишется только
# в файл (папка logs).
#
# MCP-клиент должен звать СОБРАННЫЙ файл, а не «dotnet run»: run запускает
# программу дочерним процессом и не пробрасывает ей stdin — протокол молчит.
# На Linux и macOS собранный файл называется просто VintageBotStory, без расширения.
# Строка для настроек клиента:
#   { "command": "<путь>/VintageBotStory/bin/Debug/net10.0/VintageBotStory", "args": ["--mcp"] }
set -e
cd "$(dirname "$0")"
dotnet build VintageBotStory -v q --nologo
exec ./VintageBotStory/bin/Debug/net10.0/VintageBotStory --mcp "$@"
