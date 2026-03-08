FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

COPY src/MewoDiscord.csproj ./
RUN dotnet restore

COPY src/ ./
RUN dotnet publish MewoDiscord.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "MewoDiscord.dll"]
