FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# curl нужен только здесь, чтобы забрать yt-dlp: в runtime-образ он не поедет
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
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

# Точка монтирования рабочего тома: сюда качается исходник и сюда же ложатся
# артефакты пережатия. Всё это удаляется по завершении операции
RUN mkdir -p /media

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "MewoDiscord.dll"]
