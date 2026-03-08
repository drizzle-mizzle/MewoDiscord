# MewoDiscord

Discord bot for a small friend server. Built with .NET 10.0 and [Discord.NET](https://github.com/discord-net/Discord.Net).

## Setup

1. Install [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. Copy `src/Files/config.example.ini` to `src/Files/config.ini` and fill in the bot token
3. Run:
```bash
cd src
dotnet run
```

## Publish

```bash
cd src
dotnet publish -p:PublishProfile=FolderProfile
```

Output: `publish/MewoDiscord/`
