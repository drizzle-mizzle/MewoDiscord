FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

COPY src/MewoDiscord.csproj ./
RUN dotnet restore

# .editorconfig лежит в корне репозитория: без него StyleCop в образе работает
# с настройками по умолчанию — сотни лишних предупреждений, а правила проекта не форсируются
COPY .editorconfig ./
COPY src/ ./
RUN dotnet publish MewoDiscord.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# ffmpeg — для действия convert_media (обрезка, кроп, смена формата).
# Занимает место на диске, но не память: процесс поднимается на секунды под задачу
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "MewoDiscord.dll"]
