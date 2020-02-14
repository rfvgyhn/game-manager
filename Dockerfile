FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build
WORKDIR /source/

COPY src/GameManager/*.fsproj ./
RUN dotnet restore -r linux-musl-x64

COPY src/GameManager/. ./

RUN dotnet publish -c release -o /app -r linux-musl-x64 --no-restore /p:PublishTrimmed=true
RUN rm -r /app/cs /app/de /app/es /app/fr /app/it /app/ja /app/ko /app/pl /app/pt-BR /app/ru /app/tr /app/zh-Hans /app/zh-Hant

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-alpine
WORKDIR /app
COPY --from=build /app ./
ENV DOTNET_USE_POLLING_FILE_WATCHER true
ENTRYPOINT ["./GameManager"]