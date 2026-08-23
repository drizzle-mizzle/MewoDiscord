FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# curl и unzip нужны только здесь, чтобы забрать yt-dlp и deno: в runtime-образ они не поедут
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates unzip \
    && rm -rf /var/lib/apt/lists/*

# yt-dlp — статический бинарь релиза: внутри свой python, поэтому в runtime-образе
# ни python, ни pip не нужны. Пакет из apt не годится: он заморожен на версии
# релиза Debian, а YouTube ломает yt-dlp примерно раз в месяц и починка выходит
# за считанные дни — обновляться из apt было бы некуда.
# Версия закреплена намеренно: сборка должна быть воспроизводимой. Когда бот начнёт
# отвечать «похоже, пора обновить yt-dlp» — поднять эту строку и пересобрать образ.
# Для ARM-сервера заменить yt-dlp_linux на yt-dlp_linux_aarch64
ARG YTDLP_VERSION=2026.08.19
RUN curl -fsSL -o /usr/local/bin/yt-dlp \
        "https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux" \
    && chmod 0755 /usr/local/bin/yt-dlp \
    && /usr/local/bin/yt-dlp --version

# JavaScript-рантайм для yt-dlp. YouTube отдаёт ссылки на потоки за подписью, которую
# считает его собственный js в плеере: без рантайма yt-dlp пишет «Signature solving failed»
# и «n challenge solving failed», а следом падает с «The page needs to be reloaded» —
# то есть не работает вообще ничего, хотя до YouTube он достучался.
# Скрипты-решатели (yt-dlp-ejs) уже внутри статического бинаря, не хватало только движка.
# Deno выбран потому, что yt-dlp ищет его сам, без дополнительных ключей; нужен от 2.3.0.
# Для ARM-сервера заменить x86_64 на aarch64
ARG DENO_VERSION=v2.9.5
RUN curl -fsSL -o /tmp/deno.zip \
        "https://github.com/denoland/deno/releases/download/${DENO_VERSION}/deno-x86_64-unknown-linux-gnu.zip" \
    && unzip -q -o /tmp/deno.zip -d /usr/local/bin \
    && rm /tmp/deno.zip \
    && chmod 0755 /usr/local/bin/deno

COPY src/MewoDiscord.csproj ./
RUN dotnet restore

# .editorconfig лежит в корне репозитория: без него StyleCop в образе работает
# с настройками по умолчанию — сотни лишних предупреждений, а правила проекта не форсируются
COPY .editorconfig ./
COPY src/ ./
RUN dotnet publish MewoDiscord.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# ffmpeg — для операций над медиа (обрезка, кроп, смена формата, пережатие).
# Он же склеивает раздельные видео- и аудиопотоки YouTube после скачивания.
# Занимает место на диске, но не память: процесс поднимается на секунды под задачу
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /usr/local/bin/yt-dlp /usr/local/bin/yt-dlp
COPY --from=build /usr/local/bin/deno /usr/local/bin/deno

# Проверка именно здесь, а не в build-стадии: deno слинкован динамически, и не хватать
# ему может как раз в runtime-образе, где набор библиотек другой
RUN deno --version && yt-dlp --version

# Точка монтирования рабочего тома: сюда качается исходник и сюда же ложатся
# артефакты пережатия. Всё это удаляется по завершении операции
RUN mkdir -p /media

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "MewoDiscord.dll"]
