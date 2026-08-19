# MewoDiscord

Дискорд-бот для небольшого дружеского сервера (~20 человек). Цель — оживить сервер шутливыми активностями и полезными функциями.

## Репозиторий

- GitHub: `drizzle-mizzle/MewoDiscord` (private)
- `config.ini` содержит секреты и исключён из git, в репо лежит `config.example.ini`

## Стек

- .NET 10.0, C#
- Discord.NET 3.20.1 (слеш-команды — через Interaction Framework, Components V2 — для медиа Telegram)
- OpenRouter API — ИИ-часть (модели задаются в конфиге, по секции на задачу)
- CLIProxyAPI (sidecar-контейнер `eceasy/cli-proxy-api`) — OpenAI-совместимый прокси
  к подписке ChatGPT Plus через Codex OAuth: чат и генерация изображений без API-биллинга
- BogaNet.BadWordFilter 1.4.0 — детект мата по словарю
- Serilog (консоль + файлы `logs/bot-.log`) с обёрткой BotLogger, дублирующей логи в треды Discord
- StyleCop.Analyzers (SA1513)
- xUnit — тесты в `src/MewoDiscord.Tests`

## Структура

- Решение и проект: `src/`
  - `Commands/` — слеш-команды (модули Interaction Framework)
  - `Handlers/` — обработчики событий gateway
  - `Helpers/` — конфиг, логгер, тексты сообщений
  - `Utils/` — клиенты внешних сервисов: ИИ (`AiClient` → `OpenRouterClient`),
    ChatGPT (`ChatGptClient` + `ChatGptSession`) и Telegram (`TelegramPostClient`)
  - БД сессий ChatGPT — `Helpers/ChatGptSessionStore` (файлы в `state/`), роутинг хитов —
    `Handlers/ChatGptSessionHandler`
  - `Files/` — `config.ini`, `messages.ini`, `swears.txt` (копируются в вывод)
  - `MewoDiscord.Tests/` — xUnit-тесты: фильтр мата, БД имён каналов, разбор постов Telegram,
    клиент ChatGPT
- `cliproxy/` — конфиг sidecar-прокси: `config.yaml` и `management.env` (секреты, вне git)
  плюс их example-файлы; токены Codex OAuth живут в томе `cliproxy-auth`
- Публикация: `publish/MewoDiscord/`
- Состояние рантайма: `state/voice_channels.txt` рядом с исполняемым файлом — исходные имена
  переименованных каналов (`ChannelNameStore`). Отдельно от `Files/`, потому что в Docker та внутри образа

## Возможности

Функциональная часть (работает всегда):

- Верификация: ключевое слово в заданном канале → выдача роли (`VerificationHandler`)
- Журнал голосовых каналов: вход/выход, мут/деафен, стрим, таймер сессии (`VoiceStatusHandler`)
- Переименование «одинокого» канала (`ChannelRenameWatcher`): если в публичном голосовом канале
  остался один человек, после 5 секунд тишины имя меняется на константу `AloneChannelName`
  (не конфиг) и возвращается, когда заходит кто-то ещё или канал пустеет. Устроено как вотчер
  с актором на канал: журнал сессий только сигналит ему и **никогда не ждёт** переименований
  (иначе PATCH, вставший в лимит Discord «2 переименования канала за 10 минут», задерживал бы
  логи на минуты и искажал таймер). Запросы шлются с `RetryMode.AlwaysFail` — отказ виден сразу,
  превращается в кулдаун (минута), после которого вотчер заново смотрит актуальный состав канала
  и сводит имя к нужному (eventually consistent: если за кулдаун началась новая сессия из одного
  человека — «одинокое» имя остаётся, из нескольких — возвращается родное). Бот трогает канал,
  только пока имя одно из двух ожидаемых им; стороннее имя означает вмешательство админа —
  вотчер уступает и забывает запись. Удавшаяся смена отмечается в журнале сессии канала
  (`VoiceChannelRenamed`), если он открыт. Исходные имена лежат в `state/voice_channels.txt`
  и сверяются при первом `Ready`, иначе падение бота оставило бы канал с чужим именем навсегда
- Медиа из Telegram: увидев ссылку на пост публичного канала (`t.me/канал/номер`), бот читает
  виджет-страницу `?embed=1`, скачивает видео или фото и отвечает на сообщение контейнером
  Components V2 (`TelegramMediaHandler` + `Utils/TelegramPostClient`): синяя акцентная полоса,
  имя канала ссылкой, подпись поста и медиагалерея с файлами. Контейнер выбран вместо
  embed'а потому, что видео внутри embed'а Discord ботам не отдаёт (поле `video` — только для
  входящих). Такое сообщение не может нести `content` и `embeds`, весь текст живёт в компонентах.
  Файл больше лимита сервера (`MaxUploadLimit`) не грузится — тогда шлётся обычный embed
  с превью и ссылкой. У исходного сообщения снимается стандартное превью Discord флагом
  `SuppressEmbeds` (нужно право «Управление сообщениями»). Приватные ссылки `t.me/c/...`
  не поддерживаются: их виджет требует авторизации. Работает независимо от `UseAi`
- Команды `/purge by-count`, `/purge by-time`, `/say`, `/reinstall` (сносит все глобальные и серверные
  слеш-команды, включая устаревшие, и регистрирует текущий набор; набор учитывает `UseAi`)

ИИ-часть (только при `UseAi: true`):

- Чат: бот отвечает на пинг или реплай, а также ещё несколько сообщений подряд, пока
  вспомогательная модель считает их продолжением диалога
- Цензор мата: быстрая проверка по BogaNet и `swears.txt`, затем регулярка с ИИ-верификацией;
  подтверждённые слова дописываются в `swears.txt` (словарь самообучается)
- «Накал»: повторные нарушения одного пользователя за короткий срок повышают температуру ответа
- Команды `/set temperature`, `/toggle anti-bydlo`

ChatGPT-часть (только при `UseChatGpt: true`, независима от `UseAi`):

- Клиентский слой: `ChatGptClient` ходит в sidecar CLIProxyAPI (`ChatGptProxyUrl`), который
  расходует квоту подписки ChatGPT Plus через Codex OAuth. Ручки: `ChatAsync` (текст + файлы:
  картинки уходят мультимодальными частями, текстовые вклеиваются в текст), `GenerateImageAsync`
  (с нуля и с несколькими референсами) и `ContinueImageAsync` (правка последней сгенерированной
  картинки, опционально с доп-референсами). Прокси stateless, поэтому история диалога
  и последняя картинка живут в `ChatGptSession` на стороне бота; сессия не потокобезопасна —
  вызовы сериализует владелец. Ошибки не бросаются наружу: лог + пустая строка/null
  (стиль `OpenRouterClient`). Логи запросов — в тред «ChatGPT»
- Сессии в Discord «как вкладки чатов» (`ChatGptSessionStore` + `ChatGptSessionHandler`):
  `/chatgpt new [тип: chat|image-gen]` отвечает публичным сообщением, за которым закрепляется
  сессия. Реплай на **последнее** сообщение сессии — хит в неё (так ведутся параллельные
  диалоги в одном канале); ответ бота становится новым закреплённым сообщением. Пинг бота
  в канале, где есть сессии, попадает в последнюю активную сессию канала. Реплай на старое
  сообщение сессии игнорируется — правки истории и вилки не поддерживаются. В каналах
  с сессиями обращения к боту перехватываются раньше шуточного чата (`UseAi`); в остальных
  каналах шуточный чат живёт как прежде. `/chatgpt sessions` — ephemeral-embed со списком
  (ссылка-прыжок на последнее сообщение | тип | давность), свежие сверху. Сессий не больше
  `ChatGptSessionStore.MaxSessions` — лишние вытесняются по старшинству. Долгие хиты
  выполняются в фоне под замком сессии (канальный замок не держат); в чат-сессиях ответ
  длиннее 2000 символов режется на части, привязка — на последнюю. Состояние переживает
  рестарт: индекс в `state/chatgpt_sessions.txt`, история + последняя картинка + референсы —
  в `state/chatgpt_sessions/{id}.json`
- Команды `/chatgpt-auth login` и `/chatgpt-auth status` (админские; отдельная группа,
  потому что у сабкоманд одной группы не может быть разных прав, а `/chatgpt new|sessions`
  доступны всем): OAuth-логин Codex прямо из Discord. `login` берёт ссылку у management API
  прокси (`GET /v0/management/codex-auth-url`), пользователь входит в браузере и вставляет
  redirect-URL (`localhost:1455/...` — страница не открывается, это нормально) в модалку
  по кнопке; бот отдаёт её прокси (`POST /v0/management/oauth-callback`) и опрашивает
  `get-auth-status`. State между шагами не хранится — берётся из вставленной ссылки,
  так что рестарт бота флоу не ломает. Обмен кода на токены и дальнейший их рефреш прокси
  делает сам; перелогин нужен только если OpenAI отозвал refresh-токен (при 401/403 бот
  подсказывает об этом в треде «ChatGPT»)

Все команды — только для администраторов (`DefaultMemberPermissions`), кроме публичных
`/chatgpt new` и `/chatgpt sessions`.

## Сборка, запуск, публикация

Из `src/`:
```bash
dotnet build
dotnet run
dotnet test
dotnet publish -p:PublishProfile=FolderProfile
```

Публикация: single-file, framework-dependent, win-x64. Настройки в `Properties/PublishProfiles/FolderProfile.pubxml`.

Docker (из корня репозитория) — образ собирается из `Dockerfile`, конфиг **не** попадает в образ
(исключён в `.dockerignore`), а монтируется томом read-only; логи лежат в томе `bot-logs`,
состояние — в `bot-state`:
```bash
docker compose up -d --build
```

Рядом с ботом поднимается sidecar `cliproxy` (CLIProxyAPI): его `cliproxy/config.yaml`
монтируется read-only, `cliproxy/management.env` задаёт `MANAGEMENT_PASSWORD` (включает
management API — без него `/chatgpt login` не работает), токены Codex OAuth — в томе
`cliproxy-auth`, порт 8317 наружу открыт только на `127.0.0.1` (для отладки).
Основной способ логина — команда `/chatgpt login` в Discord. Запасные пути:
device flow из консоли (код вводится из любого браузера):
```bash
docker compose run --rm cliproxy --codex-device-login
```
или логин локально (`--codex-login`) и копирование `codex-*.json` из `~/.cli-proxy-api`
в том `cliproxy-auth`. Дальше токены обновляются сами.

### Тесты

`src/MewoDiscord.Tests` — xUnit. Автономны, сеть не нужна: `Regex_*` (фильтр мата), `Store_*`
(БД имён каналов, временный каталог), `Telegram_*` (разбор виджета и поиск ссылок на фикстурах HTML),
`Watcher_*` (матрица решений переименования каналов) и `Gpt_*` (клиент ChatGPT: сборка запросов
и разбор ответов; БД сессий во временном каталоге). Классы, переставляющие общий
`AppConfig.StateDirectory`, объединены в xUnit-коллекцию "state-directory" — иначе
параллельный прогон классов гоняется за одним статиком.
Тесты `АИ_*` ходят в сеть: `АИ_Подтверждает*`/`АИ_НеПодтверждает*` — в реальный OpenRouter
(нужен рабочий `OpenRouterApiKey`), `АИ_Гпт*` — в поднятый CLIProxyAPI (нужны `UseChatGpt: true`
и заполненные `ChatGptProxy*` в `config.ini`).
Прогон без обращений к ИИ:
```bash
dotnet test --filter "FullyQualifiedName~Regex_|FullyQualifiedName~Store_|FullyQualifiedName~Telegram_|FullyQualifiedName~Watcher_|FullyQualifiedName~Gpt_"
```

## Конфигурация

- Формат INI: `Key: Value`, комментарии через `#`
- Многострочные значения: строки без `Ключ:` продолжают предыдущее значение (так записаны промпты)
- `config.ini` и `messages.ini` перечитываются на лету при изменении файла (FileSystemWatcher)
- Секции: `[COMMON]` + по секции на каждую ИИ-задачу (`AI_CHAT_SETTINGS`, `AI_CENSOR_SETTINGS`,
  `AI_SWEARS_CHECKER_SETTINGS`, `AI_CONTINUATION_CHECKER_SETTINGS`), в каждой — `Model`,
  `MaxTokens`, `Temperature`, `SystemPrompt`, `MessagePrompt`
- Плейсхолдеры промптов: `{botName}`, `{user}`, `{context}`, `{badWords}`, `{swears}`
- `UseAi` — глобальный выключатель ИИ-части. При `false` ИИ-обработчики не работают, ИИ-треды логов
  не создаются, а команды `/set` и `/toggle` не регистрируются в Discord. В отличие от остальных
  настроек фиксируется при запуске: горячая перезагрузка его не подхватывает, нужен рестарт
- `UseChatGpt` — выключатель ChatGPT-части (клиент CLIProxyAPI, его лог-тред и команды
  `/chatgpt`). Как и `UseAi`, фиксируется при запуске. `ChatGptProxyUrl`/`ChatGptProxyApiKey` —
  адрес и ключ прокси (ключ совпадает с одним из `api-keys` в `cliproxy/config.yaml`),
  `ChatGptManagementKey` — пароль management API (совпадает с `MANAGEMENT_PASSWORD`
  в `cliproxy/management.env`); все читаются на лету
- Секция `[CHATGPT_SETTINGS]` — `ChatModel`, `MaxTokens`, `ImageModel`, `ImageSize`,
  `ImageQuality`; всё перечитывается на лету
- `TelegramProxy` — прокси (`socks5://` или `http://`) для запросов к Telegram там, где он заблокирован
  провайдером; пусто — напрямую. Как и `UseAi`, применяется при запуске: `HttpClient` создаётся один раз
- Новый конфиг: свойство в AppConfig + строка в `config.ini` и `config.example.ini`
- Новое сообщение: метод в BotMessages + строка в `messages.ini`

## Конвенции

### Код
- Язык комментариев и сообщений: русский
- Все if/else/for/while/switch — обязательно со скобками (IDE0011, error)
- После `}` обязательна пустая строка (SA1513, error)
- Правила форсируются при сборке (`EnforceCodeStyleInBuild`)
- В doc-комментариях ссылаться на константы по имени, а не дублировать их значения числом —
  иначе комментарий протухает при правке константы

### Архитектура
- Обработчики событий — статические классы в `Handlers/`
- Хелперы — статические классы в `Helpers/`
- Обращения к ИИ — только через `AiClient` (он логирует запрос и ответ в тред соответствующей секции),
  не напрямую в `OpenRouterClient`. Вторая санкционированная точка входа — `ChatGptClient`:
  он сам логирует в тред «ChatGPT» и ходит в CLIProxyAPI, а не в OpenRouter
- Все тексты, видимые пользователям — через BotMessages (не хардкод). Осознанные исключения —
  `AloneChannelName` (константа в `ChannelRenameWatcher`) и тексты модалок/кнопок в атрибутах
  Interaction Framework (`[InputLabel]`, `[ModalTextInput]` требуют compile-time констант)
- Обработчики `[ComponentInteraction]`/`[ModalInteraction]` внутри модуля с `[Group]` —
  обязательно с `ignoreGroupNames: true`: иначе группа префиксует путь, и с дефолтным
  `InteractionServiceConfig` custom id никогда не совпадёт (кнопка молча не работает)
