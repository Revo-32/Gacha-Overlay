# Build only the cross-platform Backend dependency graph; never the WPF solution.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/GachaOverlay.Core/GachaOverlay.Core.csproj src/GachaOverlay.Core/
COPY src/LSOverlay.Protocol/LSOverlay.Protocol.csproj src/LSOverlay.Protocol/
COPY src/LSOverlay.Backend/LSOverlay.Backend.csproj src/LSOverlay.Backend/
RUN dotnet restore src/LSOverlay.Backend/LSOverlay.Backend.csproj
COPY src/GachaOverlay.Core/ src/GachaOverlay.Core/
COPY src/LSOverlay.Protocol/ src/LSOverlay.Protocol/
COPY src/LSOverlay.Backend/ src/LSOverlay.Backend/
RUN dotnet publish src/LSOverlay.Backend/LSOverlay.Backend.csproj \
    -c Release --no-restore --self-contained false -p:UseAppHost=false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
# Railway currently mounts Volumes as root. Keep its supported ownership model;
# do not add a root shell/chown supervisor or make credentials world-writable.
USER 0
COPY --from=build /app/publish/ ./
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "LSOverlay.Backend.dll"]
