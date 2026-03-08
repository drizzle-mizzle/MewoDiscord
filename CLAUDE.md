# MewoDiscord

Дискорд-бот для небольшого дружеского сервера (~20 человек). Цель — оживить сервер шутливыми активностями и полезными функциями.

## Репозиторий

- GitHub: `drizzle-mizzle/MewoDiscord` (private)
- `config.ini` содержит секреты и исключён из git, в репо лежит `config.example.ini`

## Стек

- .NET 10.0, C#
- Discord.NET 3.15.0
- Serilog (консоль + файлы `logs/bot-.log`)
- StyleCop.Analyzers (SA1513)

## Структура

- Решение и проект: `src/`
- Публикация: `publish/MewoDiscord/`

## Сборка, запуск, публикация

Из `src/`:
```bash
dotnet build
dotnet run
dotnet publish -p:PublishProfile=FolderProfile
```

Публикация: single-file, framework-dependent, win-x64. Настройки в `Properties/PublishProfiles/FolderProfile.pubxml`.

## Конвенции

### Код
- Язык комментариев и сообщений: русский
- Все if/else/for/while/switch — обязательно со скобками (IDE0011, error)
- После `}` обязательна пустая строка (SA1513, error)
- Правила форсируются при сборке (`EnforceCodeStyleInBuild`)

### Конфигурация
- Формат INI: `Key: Value`, комментарии через `#`
- Новый конфиг: свойство в AppConfig + строка в config.ini
- Новое сообщение: метод в BotMessages + строка в messages.ini

### Архитектура
- Обработчики событий — статические классы в `Handlers/`
- Хелперы — статические классы в `Helpers/`
- Все тексты, видимые пользователям — через BotMessages (не хардкод)
