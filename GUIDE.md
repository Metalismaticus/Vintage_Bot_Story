# Vintage Bot Story — how to run the bot

Russian original with more detail: [ЗАПУСК.md](ЗАПУСК.md). This guide covers
what a user needs; sections about the project's own test rig are left out.

## In short

One file for your system. No .NET, no batch files, no command line required.
You do need **Vintage Story installed**: the bot is not a separate game but a
client for it — it takes the game's code, block registry and compression
library from your installation.

| System | What to run |
|---|---|
| Windows | `VintageBotStory.exe` — double-click |
| Linux | `VintageBotStory` — double-click or `./VintageBotStory` |
| macOS (M1–M4) | `VintageBotStory` |
| macOS (Intel) | `VintageBotStory` |

Distribute the **whole folder**, not just the file: the `presets` folder next to
it holds the ready-made bots shown in the control panel.

## If you just want to play

1. Install **Vintage Story** (the regular game).
2. Download the folder for your system and run the file inside. .NET is not
   needed — it is inside the file.
3. The control panel opens in your browser. Pick a ready-made bot, enter a name
   (or login and password), the server address — and press **Join**.

**Starting the bot is not joining the server.** The program starts and waits for
you in the panel; it connects only when you press the button.

The panel lives **only on this machine** (`127.0.0.1`), and access is locked by a
key that is generated anew on every start. From another machine — through
`ssh -L 42462:127.0.0.1:42462 user@server`.

If the game is installed in an unusual place, say so beforehand:

```bash
export VINTAGE_STORY=/path/to/game        # Windows: set VINTAGE_STORY=C:\path
```

### What the bot does on first start

It creates a `Lib` folder next to itself with one library — `libzstd` (about
1 MB) taken from your game installation: the server sends the block registry
compressed, the game's own code decompresses it and looks for that library next
to the running program. `bot.json` (commented settings) and `logs/` appear too.

If it cannot write there (read-only folder), the bot says so **and stops**
instead of joining the server only to crash on the first compressed packet.

### Panel password

On your own machine the key in the link is enough. On a server it is not: the
link has to be copied from the console over ssh every time, it lands in browser
history, and it can only be changed by restarting. So the panel can be locked
with a **password**: a login page appears and the key in the address means
nothing anymore.

Three ways, strongest first:

```bash
# 1. A ready hash — the way to give a password to a service.
#    Only the hash ends up in the systemd unit; you cannot log in with it.
export VS_PANEL_PASSWORD_HASH='pbkdf2-sha256$210000$…$…'

# 2. The password itself in an environment variable.
export VS_PANEL_PASSWORD='your-password'
```

```jsonc
// 3. Ask at start — no-echo input, for running by hand from a terminal
{ "panel": { "use": true, "askPassword": true } }
```

Variable names can be changed in `bot.json`: `panel.passwordEnv` and
`panel.passwordHashEnv`. **The password itself is never in `bot.json`** — that
file gets shown, copied and sent around together with the bot.

If a password was requested but could not be obtained (garbage instead of a
hash, blanks in the variable, "ask at start" in a service with no console) — the
panel **does not open at all**, and the bot says why.

### Map in the panel — optional, off by default

The map lives in the **Tasks and map** tab and shows where the bot is, its home,
its storage and where it saw ore. Click a spot and the map computes the route;
the **Write to setting** button puts the point into the role's setting — it
reaches the running bot immediately, no restart. The same fields as numbers are
in the **Role settings** tab.

It is off because the bot usually lives on a server where nobody opens the panel
for weeks, and the map does work on every page poll.

```jsonc
{ "panel": { "use": true, "map": true } }
```

Or the launch flag `--map` / `--no-map`, or the checkbox in the panel.

**Meeting places of real players — a separate switch, also off:**
`{ "panel": { "map": true, "mapPeople": true } }` or `--map-people`. When on, it
shows **only the name and place of the last meeting**. The panel listens on the
loopback only and sends nothing anywhere.

### Item icons — optional, done once

Cells in the inventory show **names as words**. The bot can show pictures but can
**never draw them**: an item in Vintage Story is a 3D model rendered by the GPU
every frame, and a headless bot has no GPU. The game's own graphical client can
export them:

1. Start the regular game and enter any world (your single-player one is
   simplest). No admin rights needed — it is a client command.
2. In chat type:

   ```
   .blockitempngexport all 100
   ```

   Use `all`, not `inv`: `inv` silently skips chests, crates, cabinets, buckets
   and pies — exactly the things a storage is full of.
3. The game answers `Ok, exported to <path>` — usually the `icons` folder inside
   the game installation (`C:\Users\<you>\AppData\Roaming\Vintagestory\icons`).
4. **No copying needed**: the bot looks into that game folder itself. Keep your
   own set elsewhere with `VSBOT_ICONS=/path/to/icons` if the bot runs on
   another machine.
5. Reload the panel page. The bot rescans the folder itself (usually within
   20 seconds, at most 5 minutes).

Lookup order for the `icons` folder: `VSBOT_ICONS`; next to the sources (only
when running from a build folder); the bot's state folder (where `bot.json`
is); next to the executable; the game's own `icons` folder. The first one that
actually contains PNG files wins.

## A rented server with ssh only

### What to put there

1. **The bot** — the `out/linux-x64` folder (one file + `presets`). It builds on
   any machine, including your home Windows.
2. **The game files.** Without them the bot will not start: it loads
   `VintagestoryAPI.dll`, `VintagestoryLib.dll`, mods from `Mods` and libraries
   from `Lib`.

```bash
# from your machine where the game is installed
rsync -av --exclude assets ~/.config/Vintagestory/ server:/srv/vintagestory/
ssh server 'echo export VINTAGE_STORY=/srv/vintagestory >> ~/.profile'
```

* The whole installation is about 910 MB, 799 MB of which is `assets`.
* **Without `assets`** it is about 111 MB, and the bot works: verified. The one
  price you see at once — codes instead of human item names (`game:ingot-copper`
  instead of "copper ingot").
* Copy the game **for Linux**, not Windows: `Lib` holds libraries of the system
  the game was installed on.

**What we cannot and do not promise:** downloading the game to the server for
you. Vintage Story is paid, the key is yours.

### Running and keeping an eye on it

```bash
cd /srv/vsbot
export VS_PANEL_PASSWORD_HASH='pbkdf2-sha256$210000$…$…'
./VintageBotStory     # no arguments — control panel mode
```

Watch the panel from your machine:

```bash
ssh -L 42462:127.0.0.1:42462 user@server
# then open http://127.0.0.1:42462/ locally
```

To survive an ssh disconnect — `tmux`, `screen` or a systemd service. We do not
ship a `.service` file: every host has its own paths and user.

**Compression library on Linux:** the game code first looks for the system
`libzstd.so.1`, present on any ordinary server. If not — `apt install libzstd1`
(Debian/Ubuntu) or `dnf install libzstd` (Fedora).

### Docker

`Dockerfile` is in the root. It is built from an already published binary: you
cannot compile the bot inside the container — compilation needs the game's DLLs,
which are not and will not be in the image.

```bash
./publish.sh linux-x64
docker build -t vsbot .
docker run --rm -it --network host \
    -v /srv/vintagestory:/game:ro \
    -v /srv/vsbot-data:/data \
    -e VS_PANEL_PASSWORD='password' \
    vsbot
```

`/data` holds everything the bot writes itself (`bot.json`, session key, jobs,
meetings, logs, your presets) — set by `ENV VSBOT_STATE=/data`. Shipped presets
stay in `/app/presets`. `--network host` is needed because the panel listens on
the loopback only, by design; without it Docker adds nothing over a single
self-contained file plus `ssh -L`. Honest note: `docker build` from this file has
not been run by us — the build machine had no Docker engine; treat it as a
starting point.

## Building the files yourself

```bash
./publish.sh                # Windows: publish.bat
./publish.sh linux-x64      # one system only
```

Produces `out/win-x64`, `out/linux-x64`, `out/osx-arm64`, `out/osx-x64` — one
self-contained file per system, with `presets` next to it. Needs .NET SDK 10 and
an installed game. All four systems build from one machine; nothing
target-specific is required.

## Running from source, without batch files

```bash
dotnet build VsBotKit.slnx
dotnet run --project VintageBotStory                       # panel mode, same as a double-click
dotnet run --project VintageBotStory -- --preset digger --panel --diag
```

Running **with no arguments** equals `--panel`. The `start-bot*.bat` / `.sh`
scripts are only shortcuts for the same commands. A launch flag beats `bot.json`:
`--preset`, `--panel`, `--no-panel`, `--diag`, `--map`, `--host`, `--port`,
`--name`, `--uid`, `--account`, `--account-from-game`, `--ai`, `--quiet`.

## Role, preset and constructor — what is what

* **A role is CODE.** It can do what it can: dig quarries, trade by signs, think
  with a language model. No setting adds a new ability.
* **A ready-made bot (preset) is TEXT.** A file in `presets`: it names the role
  and all its settings. Presets can be shared — server address, name, token,
  password and AI key are never in them.
* **The role constructor is a panel tab.** There you assemble a ready-made bot:
  take a role, turn its settings and save it as a file.

Four shipped roles:

| Role | Who | Only they have |
|---|---|---|
| `survivor` | Lives on its own; on request — quarries, tunnels, ore, roads | quarry size and depth, prospecting, orders |
| `trader` | Stands in the shop, walks the counters, trades by signs | trading and the cost of dropping from height |
| `ai` | Decisions made by a language model with the same abilities a human has in chat | provider, model, effort, spending caps, character, goal and rules |
| `guard` | Stands at a post, eats from storage, tells listed players to leave. **No AI** | the Post point and leash, the list of forbidden nicknames, zone radius, what to say, whom to call, whether to hit |

Exact numbers are in the **Ready-made bots** tab: next to each role it says how
many settings it has and how many are unique to it. The **Role settings** tab
holds the role's own settings plus six that every role has but each bot sets
separately: Storage, Home, nearby chests radius and three storage-run settings.
Everything else — the **General settings** tab.

**Selecting a ready-made bot of another role swaps the role on the fly**: the
panel reports the swap and whether a restart is needed (it names anything that
did not switch cleanly). What applies immediately and what waits for the next
join is marked on the setting itself.

### Assembling your own bot in the panel

**Role constructor** tab: pick a role (or an existing preset to edit), turn the
settings — a checkbox enables an ability, a number sets a limit, three fields set
a point; every setting has its own explanation written in the role. Save under a
Latin name — that is the file name and the `--preset` name; it goes to your own
presets folder, not the shipped one (updates overwrite that). Then select it in
**Ready-made bots**.

The constructor assembles what **exists**. Whatever the bot cannot do, no
checkbox will add — that is written in code.

## The guard: what it really can do, and what nobody can

**The bot is a player, not the server.** It does not control other people's
movement: there is no client-side way to stop a player, and bodies do not block
each other. Everything that truly "keeps people out" is done by the SERVER: ban,
kick, claim privileges. The role does not promise that.

What the guard does: sees who is nearby and reads names; addresses the visitor
by name in chat ("Grisha_Thief, you are not allowed here"); calls the owner by
nickname; **hits only if explicitly told to** by the "Hit" setting (default: no).

```bash
VintageBotStory --preset guard --panel
```

Fill in three things: the **Post** (Role settings → Guard, a click on the map,
or `!пост тут` in chat; empty Post means the zone is around Home), the **list of
nicknames** (field "ЧужиеНики", comma-separated, compared whole and
case-insensitively), and **your nickname** (field "Хозяин"). For food set
**Storage** (or `!склад тут` standing at a chest). `!стража` in chat reports the
post, the zone, whom it keeps out and whether it may hit.

Chat commands are Russian words with `!` — the panel lists every command of the
running role with its description.

## Your own role in code

A role is a class derived from `BotRole`. The bot looks for your roles as **built
files** (`*.dll`) in a `roles` folder and adds them to the same list as the four
shipped ones: visible in the panel, selectable by preset, configured by the same
constructor. There is no compiler inside the bot — you build on your side.

1. `dotnet new classlib -o MyRole`
2. Reference the bot library in `MyRole.csproj` — as a project if you have the
   sources, as a file otherwise:

   ```xml
   <ItemGroup>
     <Reference Include="VsBotKit">
       <HintPath>path\to\VsBotKit.dll</HintPath>
       <Private>false</Private>
     </Reference>
   </ItemGroup>
   ```

3. The smallest role:

   ```csharp
   using VsBotKit;

   namespace MyRoles;

   public class Watchman : BotRole
   {
       public override string Name => "Watchman";

       // A writable public property is a SETTING of the role:
       // it shows up in the panel and goes into the preset by itself.
       public string ForbiddenNicknames { get; set; } = "";

       public override void Configure(VsBot bot)
       {
           bot.Survive();        // survival reflexes in one line
           bot.CommonCommands(); // the usual chat commands
       }
   }
   ```

4. `dotnet build -c Release` and copy `bin/Release/net10.0/MyRole.dll` into
   `<bot folder>/roles/`.
5. Restart the bot. The **Ready-made bots** tab lists it and says what was taken
   and what was rejected.

Requirements, each reported aloud on failure: public class, not abstract, a
parameterless constructor, derived from `BotRole`. The preset name is the class
name in lower case. Settings are writable public properties: flag, number,
string, enum (becomes a list), a point of three numbers (`GamePos?`,
`BlockPos?`). `[KnobPlace("работа")]` names the panel section; the
`/// <summary>` becomes the explanation under the field.

Lookup order for `roles`: `VSBOT_ROLES`; next to the sources (build folder
only); the bot's state folder; next to the executable.

**A file in `roles` is CODE running in the same process as the bot.** No
sandbox. Put there only what you built yourself. No hot reload — restart after
copying. Verified on Windows; not yet run on Linux or macOS.

## What this does NOT do

- **Does not install the game.** The bot is a client for it, not a separate game.
- **Does not work without the game installed**, and there is no way around it:
  packet decompression, the block registry and physics are the game's code.
- **Never stores the account password.** Only the `VS_PASSWORD` environment
  variable or input in the panel that passes through and is forgotten. Only the
  session key is saved — `botsession.json`, mode 600 on Unix.
- **The AI key is the only secret the panel really writes to disk** —
  `botsecrets.json` in the state folder, mode 600 on Unix, not encrypted (it
  would have to be encrypted with a key lying next to it). It is never shown back:
  the panel says only "key present" or "NO KEY". `ANTHROPIC_API_KEY` in the
  environment takes precedence.
- **Verified by building and running on Windows.** Files for all four systems are
  built from one Windows machine; on Linux and macOS nobody has run it live yet.

## License

Free for personal, non-commercial use. Any commercial use — embedding into a
game, mod or product, selling, paid services — requires a written license from
the copyright holder. See [LICENSE](LICENSE).
