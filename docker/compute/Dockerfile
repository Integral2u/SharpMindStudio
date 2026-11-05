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

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    SHARPMIND_DATA=/data/SharpMind \
    SHARPMIND_MODELS_DIR=/data/SharpMind/Models \
    SHARPMIND_PLUGINS=/app/plugins

EXPOSE 8080
VOLUME ["/data/SharpMind"]

ENTRYPOINT ["dotnet", "SharpMind.Studio.ComputeHost.dll"]
