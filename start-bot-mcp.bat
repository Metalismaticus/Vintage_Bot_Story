@echo off
title Vintage Bot Story — MCP-сервер (проверка модов)
rem Ботом управляет внешний MCP-клиент (Claude Code, редактор): он водит бота
rem по миру своими руками и видит настоящий результат. Удобно гонять моды.
rem
rem ВНИМАНИЕ: в этом режиме stdout занят протоколом — журнал пишется только
rem в файл (папка logs).
rem
rem MCP-клиент должен звать СОБРАННЫЙ EXE, а не "dotnet run": run запускает
rem программу дочерним процессом и не пробрасывает ей stdin — протокол молчит.
rem Проверено живьём. Строка для настроек клиента:
rem   { "command": "<путь>\\VintageBotStory\\bin\\Debug\\net10.0\\VintageBotStory.exe",
rem     "args": ["--mcp"] }
chcp 65001 >nul
dotnet build "%~dp0VintageBotStory" -v q --nologo
"%~dp0VintageBotStory\bin\Debug\net10.0\VintageBotStory.exe" --mcp
pause
