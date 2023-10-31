FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS restore
WORKDIR /source/

COPY src/GameManager/*.fsproj ./
RUN dotnet restore -r linux-musl-x64

COPY src/GameManager/. ./

FROM restore as build
RUN dotnet publish -c release -o /app -r linux-musl-x64 --no-restore
RUN rm -r /app/cs /app/de /app/es /app/fr /app/it /app/ja /app/ko /app/pl /app/pt-BR /app/ru /app/tr /app/zh-Hans /app/zh-Hant

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine-amd64
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["./GameManager"]