# MewoDiscord

Discord bot for a small friend server. Built with .NET 10.0 and [Discord.NET](https://github.com/discord-net/Discord.Net).

## Features

- **Voice activity log** — posts joins, leaves, mute/deafen, streaming and a session timer to a status
  channel (public voice channels get their own thread) or into the voice channel itself if it is private
- **Lonely channel rename** — when someone is left alone in a public voice channel, its name is replaced
  with a fixed phrase after 5 seconds and restored as soon as anyone else joins or the channel empties;
  original names are persisted to disk, so a crash cannot leave a channel stuck with the wrong name
- **Telegram media** — when a message contains a link to a post in a public Telegram channel, the bot
  fetches the post, downloads its video or photos and replies with a blue Components V2 container that
  plays the media inline next to the post caption; files above the server upload limit are linked with a
  preview instead, and Discord's own link preview is suppressed on the original message
- **Verification** — a keyword in the verification channel grants the configured role
- **Admin slash commands** — bulk message deletion, speaking as the bot and reinstalling the command list
- **AI chat** — replies when pinged or replied to, and keeps the conversation going for a few messages
- **AI profanity censor** — answers profanity with a snarky one-liner; dictionary hits are handled right
  away, while regex hits are first verified by a small model and then appended to the dictionary

Everything AI-related is optional and can be turned off with a single config flag (see `UseAi` below).

## Setup

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. Copy `src/Files/config.example.ini` to `src/Files/config.ini`
3. Fill in the `[COMMON]` section:

   | Key | Required | Description |
   |---|---|---|
   | `BotToken` | yes | Bot token; the bot refuses to start without it |
   | `UseAi` | no (default `true`) | Master switch for all AI features and the `/set`, `/toggle` commands |
   | `OpenRouterApiKey` | if `UseAi: true` | [OpenRouter](https://openrouter.ai) key used for every AI request |
   | `VoiceStatusChannel` | no | Text channel ID for the voice activity log; `0` disables it for public voice channels (private ones always log into themselves) |
   | `LogsChannel` | no | Text channel ID for bot logs and AI request threads; `0` disables it |
   | `VerificationChannel`, `VerificationRole` | no | Channel ID and role ID for verification; both must be non-zero |
   | `LocalTimeZone` | no | IANA time zone used by `/purge by-time` and log timestamps |
   | `TelegramProxy` | no | Proxy for Telegram requests (`socks5://host:port` or `http://host:port`), needed where Telegram is blocked by the ISP; empty means a direct connection |

   The `AI_*` sections below configure one model, token limit, temperature and prompt pair per task.

4. Run:
```bash
cd src
dotnet run
```

`config.ini` and `messages.ini` are re-read while the bot is running, so texts, models and prompts can be
tweaked without a restart. `UseAi` is the exception — it is read once at startup.

## Commands

All commands require the Administrator permission.

| Command | Description |
|---|---|
| `/purge by-count` | Delete the last N messages (1–100), optionally only from one user |
| `/purge by-time` | Delete messages in a `yyyy-MM-dd HH:mm` range, optionally only from one user |
| `/say` | Post a message as the bot |
| `/reinstall` | Wipe every registered slash command, including stale ones, and register the current set |
| `/set temperature` | Set the temperature of the censor and swear-checker models (requires `UseAi: true`) |
| `/toggle anti-bydlo` | Turn the AI profanity censor on or off (requires `UseAi: true`) |

Discord does not allow bulk-deleting messages older than 14 days; both purge commands report how many
messages were skipped for that reason.

## Tests

```bash
cd src
dotnet test --filter "FullyQualifiedName~Regex_|FullyQualifiedName~Store_|FullyQualifiedName~Telegram_"
```

The `Regex_*` (profanity filter), `Store_*` (channel-name database) and `Telegram_*` (widget parsing and
link detection) tests are self-contained and need no network. The `АИ_*` tests call OpenRouter for real and
need a working `OpenRouterApiKey`; run them only when you want to check the AI verification step.

## Docker

The image is built from source; `config.ini` is kept out of the image and mounted read-only instead.
Logs live in the `bot-logs` volume and the original-channel-name database in `bot-state`, so both survive
`docker compose up --build`.

```bash
docker compose up -d --build
```

## Publish

```bash
cd src
dotnet publish -p:PublishProfile=FolderProfile
```

Output: `publish/MewoDiscord/` (single-file, framework-dependent, win-x64).
