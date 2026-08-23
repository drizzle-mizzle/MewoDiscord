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
- **ChatGPT sessions** — `/chatgpt new` pins a session to the bot's reply; replying to that message
  continues the conversation, so several chats can run side by side in one channel. Requests go through
  a CLIProxyAPI sidecar backed by a ChatGPT Plus subscription, and the model decides on its own whether
  to answer with text or draw a picture, exactly like the web UI
- **Custom AI actions** — a ping can mean "do it", not just "talk". A cheap system gate runs first
  (no network), then a per-action prompt goes to a cheap instant model that answers yes or no; the first
  action that hits takes over. Actions are flat ini files in `Files/custom_ai_actions/`, one per file,
  each paired with a processor. The one shipped today edits a mentioned user's avatar: it downloads the
  picture, opens a regular ChatGPT session around it and asks for the edit, so follow-up tweaks are
  ordinary replies. A second action does mechanical media work — trimming, cropping,
  resizing and format changes — where the model only translates the phrasing into a typed plan and
  ffmpeg does the work under hard limits

The former OpenRouter AI features (joke chat and the profanity censor) are dormant: the code is still
there, but its entry points, settings and commands are disabled pending a rewrite on top of the proxy
API. Their prompts are archived in `src/Files/ai_prompts.legacy.ini`.

## Setup

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. Copy `src/Files/config.example.ini` to `src/Files/config.ini`
3. Fill in the `[COMMON]` section:

   | Key | Required | Description |
   |---|---|---|
   | `BotToken` | yes | Bot token; the bot refuses to start without it |
   | `VoiceStatusChannel` | no | Text channel ID for the voice activity log; `0` disables it for public voice channels (private ones always log into themselves) |
   | `LogsChannel` | no | Text channel ID for bot logs and the ChatGPT request thread; `0` disables it |
   | `VerificationChannel`, `VerificationRole` | no | Channel ID and role ID for verification; both must be non-zero |
   | `LocalTimeZone` | no | IANA time zone used by `/purge by-time` and log timestamps |
   | `UseChatGpt` | no (default `false`) | Master switch for the ChatGPT part: sessions, the `/chatgpt` commands and the log thread |
   | `ChatGptProxyUrl`, `ChatGptProxyApiKey` | if `UseChatGpt: true` | Address and client key of the CLIProxyAPI sidecar |
   | `ChatGptManagementKey` | if `UseChatGpt: true` | Password of the proxy management API, used by `/chatgpt-auth login` |

   The `[CHATGPT_SETTINGS]` section holds the chat model, token limit and system prompt.

4. Run:
```bash
cd src
dotnet run
```

`config.ini` and `messages.ini` are re-read while the bot is running, so texts, models and prompts can be
tweaked without a restart. `UseChatGpt` is the exception — it is read once at startup.

## Commands

All commands require the Administrator permission, except `/chatgpt new` and `/chatgpt sessions`,
which are open to everyone.

| Command | Description |
|---|---|
| `/purge by-count` | Delete the last N messages (1–100), optionally only from one user |
| `/purge by-time` | Delete messages in a `yyyy-MM-dd HH:mm` range, optionally only from one user |
| `/say` | Post a message as the bot |
| `/reinstall` | Wipe every registered slash command, including stale ones, and register the current set on this guild |
| `/chatgpt new` | Start a ChatGPT session pinned to the bot's reply (anyone can use it; refuses until an account is connected) |
| `/chatgpt sessions` | List sessions with jump links to their latest messages (anyone can use it) |
| `/chatgpt-auth login` | Sign in to the ChatGPT account through the proxy's OAuth flow |
| `/chatgpt-auth status` | Show the accounts currently connected to the proxy |

Discord does not allow bulk-deleting messages older than 14 days; both purge commands report how many
messages were skipped for that reason.

## Tests

```bash
cd src
dotnet test --filter "FullyQualifiedName~Regex_|FullyQualifiedName~Store_|FullyQualifiedName~Telegram_|FullyQualifiedName~Watcher_|FullyQualifiedName~Gpt_"
```

The `Regex_*` (profanity filter), `Store_*` (channel-name database), `Telegram_*` (widget parsing and
link detection), `Watcher_*` (channel rename decisions) and `Gpt_*` (ChatGPT client and session database)
tests are self-contained and need no network. The `АИ_Гпт*` tests talk to a running CLIProxyAPI; the
remaining `АИ_*` ones target the dormant OpenRouter code and only work if its sections are restored
to `config.ini`.

## Docker

The image is built from source; `config.ini` is kept out of the image and mounted read-only instead.
Logs live in the `bot-logs` volume and runtime state (channel names, ChatGPT sessions) in `bot-state`,
so both survive `docker compose up --build`. A `cliproxy` sidecar (CLIProxyAPI) runs next to the bot and
serves the ChatGPT part; its config comes from `cliproxy/config.yaml`, the management password from
`cliproxy/management.env`, and Codex OAuth tokens live in the `cliproxy-auth` volume.

```bash
docker compose up -d --build
```

Sign in to the ChatGPT account with `/chatgpt-auth login` in Discord. As a fallback, the proxy supports a
console device flow:

```bash
docker compose run --rm cliproxy --codex-device-login
```

## Publish

```bash
cd src
dotnet publish -p:PublishProfile=FolderProfile
```

Output: `publish/MewoDiscord/` (single-file, framework-dependent, win-x64).
