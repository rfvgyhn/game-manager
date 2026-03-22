FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY src/GameManager/*.fsproj ./
RUN dotnet restore -r linux-x64
COPY src/GameManager/. ./

FROM restore AS build
RUN dotnet publish -c release -o /app -r linux-x64 --no-restore --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build --chown=1654:1654 /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["./GameManager"]