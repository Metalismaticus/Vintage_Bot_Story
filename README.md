# Vintage Bot Story

A headless bot for Vintage Story 1.22.7. It joins a server as an ordinary player
and lives in the world by a player's rules — walks, digs, builds, eats, sleeps,
fights back, trades. Body physics is computed by the game's own code; the bot
does nothing a live player could not do.

- **How to run, configure and control it — [GUIDE.md](GUIDE.md)** (English) ·
  [ЗАПУСК.md](ЗАПУСК.md) (Russian original, more detail).
- Connecting a language model, or handing the bot's body to an external model
  over MCP — [AI.md](AI.md) (English) · [ИИ.md](ИИ.md) (Russian).
- Ready-made bots — the `presets/` folder: survivor (`digger`, `quarry-only`,
  `resident`), trader (`trader`), guard (`guard`), AI trader (`ai`).

## Run

You need **Vintage Story installed** — the bot takes the game's assemblies,
block registry and compression library from it. Then either:

- **the published single file** for your system (`VintageBotStory.exe` on Windows,
  `VintageBotStory` on Linux/macOS) — double-click, no .NET and no batch files
  required; the control panel opens in your browser; or
- **from source** with .NET 10 SDK:

```bash
dotnet build VsBotKit.slnx
dotnet run --project VintageBotStory        # opens the control panel in the browser
./publish.sh                                # one self-contained file per system, in out/
```

Running with no arguments equals `--panel`; the `start-bot*.bat` / `.sh` files
are only shortcuts for the same commands. If the game is installed in an unusual
place: `-p:VintageStoryPath=/path/to/game` when building, or the `VINTAGE_STORY`
environment variable at run time.

This repository holds only what is needed to build and run. The test suite, the
test server and the project's working documents are not published here.

## License

Source is open to read and **free for personal, non-commercial use**: install the
bot for yourself, play with it, study and modify it for yourself
([LICENSE](LICENSE), PolyForm Noncommercial 1.0.0).

**Any commercial use is prohibited without a written agreement with the
author**: embedding into a game, mod or product, selling, paid services built on
this code, use inside a company. For that, contact the copyright holder:
https://github.com/Metalismaticus.

Copyright © 2026 Metalismaticus. All rights reserved except those expressly
granted by the license.

---

По-русски: запуск и настройка — [ЗАПУСК.md](ЗАПУСК.md); нейросеть и MCP —
[ИИ.md](ИИ.md). По-английски: [GUIDE.md](GUIDE.md), [AI.md](AI.md). Код
бесплатен для личного некоммерческого использования; любое коммерческое —
только по письменному соглашению с автором.
