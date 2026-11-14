# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props JellyEncore.sln ./
COPY src/Encore/Encore.csproj src/Encore/
COPY tests/Encore.Tests/Encore.Tests.csproj tests/Encore.Tests/
RUN dotnet restore src/Encore/Encore.csproj
COPY src/ src/
RUN dotnet publish src/Encore/Encore.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /out .

# /data holds config and SQLite. World-writable so it works whatever uid compose runs us as
# (Unraid uses user: "99:100"); that uid has no passwd entry, so HOME points somewhere writable.
RUN mkdir -p /data && chmod 777 /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/data
VOLUME /data
EXPOSE 8080
USER 64198:64198

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD ["dotnet", "/app/Encore.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/Encore.dll"]
