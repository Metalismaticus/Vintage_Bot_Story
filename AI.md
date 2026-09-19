# Connecting an AI to drive the bot

Russian original: [ИИ.md](ИИ.md).

In short: **yes, and there are two ways.** Both are written; each is enabled by
one setting and one launch flag.

| Way | Who decides | When |
|---|---|---|
| **The `ai` role** (`--preset ai` or `--ai`) | a language model **inside** the bot: it looks around, walks and answers in chat by itself | the bot lives on its own — trades, guards, chats with players |
| **MCP mode** (`--mcp`) | a model **outside**: Claude Code or an editor drives the bot by hand | checking mods, one-off experiments, "go there and see what happened" |

What they share is the main thing: **both call the SAME abilities a human uses in
chat and in the control panel.** There is no separate "brain for the AI" — the
list of abilities is one (`VsBotKit/Agent/BotTools.cs`, `BotToolbox.Standard`),
and the **Abilities** tab of the panel shows the same list.

---

## 1. The `ai` role: the bot thinks for itself

### Where the AI settings live

**In one place — the settings of the `ai` role.** Provider, model, reasoning
effort, conversation length and spending caps live together
(`VsBotKit/AiSettings.cs`), and the panel shows them among the role's other
settings.

**The key.** Two ways, and the environment variable wins:

- the **Key** field in the **Role settings** tab, card "Neural network" — the
  only secret the panel really writes to disk: `botsecrets.json` in the bot's
  state folder (next to `bot.json`), mode 600 on Unix, not encrypted. It is never
  shown back: the panel says only "key present" or "NO KEY"; it never gets into
  the log, the chat or a preset file. An empty field means "forget";
- the environment variable from the provider table below (`ANTHROPIC_API_KEY`
  for `anthropic`). If it is set, it is used and the saved one waits; the panel
  says so.

A running bot does not pick up a new key: the client reads it once, when it is
created — restart after changing.

### Supported providers

The list is **data**, not text in this guide: the panel draws it as a dropdown,
and in chat the same list is printed by

```
!ии поставщики
```

which also says for each provider whether a key is set **right now** (yes/no —
the value itself is never printed).

| Name | Who | Key variable | Example models |
|---|---|---|---|
| `anthropic` | Anthropic (Claude), console.anthropic.com | `ANTHROPIC_API_KEY` | `claude-opus-5`, `claude-sonnet-5`, `claude-haiku-4-5` |
| `anthropic-token` | the same Anthropic, by a temporary login token | `ANTHROPIC_AUTH_TOKEN` | the same |
| `совместимый` ("compatible") | any service speaking the Anthropic Messages protocol | `ANTHROPIC_API_KEY` + address in `ANTHROPIC_BASE_URL` | ask the service |
| `своя` ("own") | your own model connected in code (two methods of `ILlmClient`) | none | yours |

**What is not in the list and why.** The bot has **one** client, and it speaks the
Anthropic Messages protocol. These rows are visible in the panel but cannot be
chosen, and the reason is written next to them:

| Name | Why not supported |
|---|---|
| `bedrock` | Claude via AWS: needs another client (`Anthropic.Bedrock`) and AWS-signed requests |
| `vertex` | Claude via Google Cloud: needs `Anthropic.Vertex` and `gcloud` login |
| `openai` | OpenAI, Gemini and others: a different protocol. Connect in code — see `своя` |

**`совместимый` has not been tried live**: the SDK passes the address from
`ANTHROPIC_BASE_URL` to the network by itself, and no request has gone that way
from this project yet.

### How to enable

**Step 1. Provide the key** — in the panel field or in the environment variable:

```bat
rem Windows, for one launch
set ANTHROPIC_API_KEY=sk-ant-...
```

```bash
# Linux / macOS, for one launch
export ANTHROPIC_API_KEY=sk-ant-...
```

Making it survive a reboot (`setx` on Windows, `~/.profile` on Linux/macOS) is
your decision; the bot takes no part in it.

**Step 2. Choose the role.** Any of these is fine:

```bash
VintageBotStory --preset ai
dotnet run --project VintageBotStory -- --ai
```

```jsonc
// bot.json next to the bot
{ "ai": true }
```

Or pick the ready-made bot **ИИ** in the panel and press **Join**.

### What you see if something is wrong

The bot does not stay silent. Before joining, the log gets either

```
[ии] думаю: поставщик Anthropic (Claude), модель claude-opus-5, усилие low,
     потолок ответа 8192 токенов, память 40 сообщений, раздумье раз в 60 с
```

("thinking: provider …, model …, effort …, answer cap …, memory …, thinking every
60 s") — or an honest refusal where **every problem is its own line**:

```
[ии] НЕ ДУМАЮ: ключа нет: переменная окружения ANTHROPIC_API_KEY пуста
[ии] НЕ ДУМАЮ: модель не названа: настройка «Модель» пуста
```

("NOT THINKING: no key …", "model not named …"). Three more exist: provider not
chosen, unknown provider (with the list), address not set (only for
`совместимый`).

In the second case the bot **still joins the world and lives**: survival reflexes
work, chat commands work. It just does not spend money on requests known to fail.

Chat commands (Russian words):

```
!ии              provider and model, how much spent, whether it may keep spending
!ии поставщики   the whole list: name, key variable, key present?, example models
!ии стоп         stop thinking (the bot stays alive)
!ии пуск         think again (re-checking the key)
!ии забудь       forget what was spent; limits count from zero
```

### Settings

All are **properties of the role**, so the panel shows them by itself. They can
be turned while running; character, goal and rules reach the model a couple of
seconds after an edit.

| Setting | Default | Meaning |
|---|---|---|
| `Характер` (character) | an ordinary player, speaks briefly | who this bot is and how it talks |
| `Цель` (goal) | stay near home, look after itself, answer players | what it does when left alone |
| `Правила` (rules) | empty | the owner's prohibitions |
| `ДуматьРазВСекунд` | 60 for the `ai` role (45 in the base class) | how often to think without a reason (0 — only on events) |
| `ОтвечатьВЧате` | yes | answer players' messages |
| `ДействийЗаХод` | 12 | actions in a row per turn |
| `РефлексыВыживания` | yes | attach survival reflexes (read once, at role selection) |
| `Поставщик` (provider) | `anthropic` | who provides the model; list — `!ии поставщики` |
| `Модель` (model) | `claude-opus-5` | which model thinks |
| `Усилие` (effort) | `low` | `low`, `medium`, `high`, `max` |
| `ПотолокОтвета` | 8192 | tokens per turn; thinking and text share it |
| `ПомнитьСообщений` | 40 | conversation length; more memory — each turn costs more |
| `Магазин` (shop) | no | raise the shop and add two abilities on top |
| `ОбращенийВЧас` | 60 | hard cap on requests per hour |
| `ТокеновВСутки` | 1 000 000 | hard cap on tokens per day |
| `ЦенаВходаЗаМиллион` / `ЦенаВыходаЗаМиллион` | 0 (unknown) | enter the tariff — the bot computes spending in money |
| `Валюта` (currency) | `$` | label in the summary |

**About `Магазин`.** Off by default. Turn it on and the shop mechanism comes up,
the abilities `shop_status` and `shop_round` appear, and the character switches to
the shopkeeper (unless you rewrote it). It does not switch back: the trader
ability is already in the bot's body, and the role says so instead of pretending.

### What it can do

Exactly what a human can: about thirty abilities (the exact list — **Abilities**
tab of the panel). Look around, speak, walk, climb with a pillar or a ladder,
descend, read signs, honestly open chests, hold an item, break and place blocks,
hit, eat, gather berries, make a campfire, hide from a storm, wait. Plus all the
digging work: an order "bring twenty copper", quarry, tunnel, prospecting
underground, road, hauling to storage, checking and replenishing supplies.

### What it can NOT do

- **Nothing beyond a human.** No teleport, no seeing through walls, no contents
  of a closed chest, no instant mining. Everything dishonest lives behind the
  `Cheats` flag, off by default, and is not given to the model.
- **Save itself.** Not hanging in the air, fighting off a wolf, eating, healing,
  hiding from a storm — these are deterministic reflexes. The model must not
  think for three seconds while the bot is being eaten.
- **Work without a key.** An honest refusal, not silent idling.
- **Change reflexes on the fly.** `РефлексыВыживания` is read once.

### What it costs

Every "think" is a paid request. One turn = system prompt + all ability
descriptions + conversation history + a snapshot of the surroundings + the
model's answer; the first turn is more expensive (empty cache). With
`ДуматьРазВСекунд = 60` the bot thinks once a minute out of boredom plus on every
player message and important event. Default caps: **60 requests per hour** and
**a million tokens per day**; at 80 % the bot thinks four times less often, at
the cap it stops and says so in the log.

Money is counted only if you enter the tariff. Check current prices with the
provider and put them into `ЦенаВходаЗаМиллион` / `ЦенаВыходаЗаМиллион`; without
them the bot says "not counting the price" rather than inventing one.

Cheapest: `Усилие = low` (default), smaller `ПомнитьСообщений`, larger
`ДуматьРазВСекунд`, a simpler model. Most expensive: `Усилие = max` and a long
memory.

---

## 2. MCP mode: an external client drives the bot

The bot becomes an MCP server and offers its abilities to any MCP client —
Claude Code, an editor, your own script. Exchange goes over the process's
stdin/stdout, so **in this mode the log is written only to a file** (`logs`
folder): stdout carries the protocol, and one stray word there breaks the link.

### How to enable

```bash
dotnet build VintageBotStory
```

and put the **built exe, not `dotnet run`** into the MCP client's settings
(`run` starts the program as a child process and does not pass stdin through —
the protocol stays silent; verified live):

```json
{
  "command": "C:\\path\\to\\VintageBotStory\\bin\\Debug\\net10.0\\VintageBotStory.exe",
  "args": ["--mcp"]
}
```

Ready shortcut — `start-bot-mcp.bat` (and `start-bot-mcp.sh`).

### What the client gets

The same set of abilities plus four for mod development:

| Ability | Purpose |
|---|---|
| `inspect_block` | everything the bot knows about a block, including a mod's block-entity data |
| `watch_block` | wait until the block changes — checking a mod's reaction |
| `server_command` | send a server command as the bot (works only if the bot has the rights; this is ordinary chat, not a cheat) |
| `chat_tail` | the last lines of game chat — where the server answers commands |

### Honest refusal

The **MCP** role can also be picked in the panel — but the server **will not come
up**, because stdin is taken by the launch. The log then says:

```
[mcp] ВНЕШНЕГО КЛИЕНТА НЕТ: роль выбрана без ключа --mcp, сервер не поднят.
```

("NO EXTERNAL CLIENT: role chosen without --mcp, server not started."). In chat
the same is asked with `!mcp`.

Role settings: `Здороваться` (greeting — a shared setting of all living roles; the
MCP default is off, extra noise is useless in mod checks) and `РадиусСбораЕды`
(food-gathering radius, 16 blocks by default: the client left the bot at its
block and expects it there, not thirty cells away at a raspberry bush).

**This role has no AI key and must not have one** — whoever connects pays for the
thinking. Provider, model and spending caps belong to the `ai` role.

---

## 3. What this is NOT

- **Not "the bot learns everything by itself".** The model only chooses what to
  do and when, from the ready list of abilities. A new ability is code in the
  library, not a request to the model.
- **Not free.** See the cost section.
- **Not tried live at the time of writing.** No request to a model has been made
  from this project: it is a paid call on the owner's key. Built, covered by
  tests of conversation parsing, budget and settings — the first real run is
  yours. This includes the `совместимый` provider.
- **Not a showcase of every provider.** One client, one protocol — Anthropic
  Messages. Bedrock, Vertex, OpenAI and Gemini are visible in the list but cannot
  be chosen, and what is missing for them is written next to each.
- **Not protection from a foreign client.** Whoever connects to the MCP server
  over stdio drives the bot. Install it as carefully as any other developer tool.

## 4. Where things are

| File | What |
|---|---|
| `VsBotKit/AiSettings.cs` | THE place for AI settings: providers as data, readiness check, spending cap |
| `VsBotKit/Agent/BotTools.cs` | the list of abilities — one for the model, the panel and MCP |
| `VsBotKit/Agent/BotSnapshot.cs` | the surroundings in words: health, time, who is near, signs |
| `VsBotKit/Agent/LlmClient.cs` | the model interface: your own model is two methods |
| `VsBotKit/Agent/ClaudeClient.cs` | Claude through the official Anthropic SDK |
| `VsBotKit/Agent/LlmBudget.cs` | spending caps, sliding windows, soft degradation |
| `VsBotKit/Agent/LlmRole.cs` | the thinking loop, world events, actions-per-turn limit |
| `VsBotKit/Agent/McpServer.cs` | JSON-RPC 2.0 over stdio + mod-checking abilities |
| `VintageBotStory/AiTraderRole.cs` | the `ai` role: character, goal, rules, what to think with, shop |
| `VintageBotStory/McpRole.cs` | the role under external control |
