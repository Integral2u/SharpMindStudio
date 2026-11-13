# syntax=docker/dockerfile:1.7
# Package build. Publish the SharpMind packages before using this image for a
# clean public/CI build.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props SharpMind.References.props ./
COPY SharpMind.Studio.Core/ SharpMind.Studio.Core/
COPY SharpMind.Studio.ComputeHost/ SharpMind.Studio.ComputeHost/

RUN dotnet restore SharpMind.Studio.ComputeHost/SharpMind.Studio.ComputeHost.csproj
RUN dotnet publish SharpMind.Studio.ComputeHost/SharpMind.Studio.ComputeHost.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY docker/entrypoint-drop.sh /app/entrypoint.sh
# Start as root only long enough to fix mounted-folder ownership, then drop to
# the unprivileged sharpmind user (UID 10001).
RUN sed -i 's/\r$//' /app/entrypoint.sh \
    && chmod +x /app/entrypoint.sh \
    && groupadd --gid 10001 sharpmind \
    && useradd --uid 10001 --gid 10001 --create-home --shell /usr/sbin/nologin sharpmind \
    && (getent group video >/dev/null && usermod -aG video sharpmind || true) \
    && mkdir -p /app/plugins /data/SharpMind \
    && chown -R sharpmind:sharpmind /app /data/SharpMind

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    SHARPMIND_DATA=/data/SharpMind \
    SHARPMIND_MODELS_DIR=/data/SharpMind/Models \
    SHARPMIND_PLUGINS=/app/plugins

EXPOSE 8080
VOLUME ["/data/SharpMind"]

ENTRYPOINT ["/app/entrypoint.sh", "dotnet", "SharpMind.Studio.ComputeHost.dll"]
