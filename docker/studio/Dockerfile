# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The SharpMind packages come from NuGet. The preview package feed is not
# copied here: publish the 1.1.0 packages before building the public image.
COPY Directory.Build.props Directory.Packages.props SharpMind.References.props ./
COPY SharpMind.Studio.Core/ SharpMind.Studio.Core/
COPY SharpMind.Studio/ SharpMind.Studio/

RUN dotnet restore SharpMind.Studio/SharpMind.Studio.csproj
RUN dotnet publish SharpMind.Studio/SharpMind.Studio.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production \
    Studio__Root=/data/SharpMind \
    Studio__Url=http://0.0.0.0:5117 \
    Studio__LanUrl=http://0.0.0.0:5118

EXPOSE 5117 5118
VOLUME ["/data/SharpMind"]

ENTRYPOINT ["dotnet", "SharpMind.Studio.dll"]
