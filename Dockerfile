FROM mcr.microsoft.com/dotnet/sdk:7.0-alpine AS build
WORKDIR /source/

COPY src/GameManager/*.fsproj ./
RUN dotnet restore -r linux-musl-x64

COPY src/GameManager/. ./

RUN dotnet publish -c release -o /app -r linux-musl-x64 --no-restore --self-contained false
RUN rm -r /app/cs /app/de /app/es /app/fr /app/it /app/ja /app/ko /app/pl /app/pt-BR /app/ru /app/tr /app/zh-Hans /app/zh-Hant

FROM mcr.microsoft.com/dotnet/aspnet:7.0-alpine-amd64
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app ./
ENV DOTNET_USE_POLLING_FILE_WATCHER true
ENTRYPOINT ["./GameManager"]