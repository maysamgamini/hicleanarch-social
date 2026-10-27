# The gateway's image: a small proxy in front of the monolith (s3-l14). Built from the repository root:
#   docker build -f src/Gateway/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig NuGet.config BuildingBlocks.slnx ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Gateway/ src/Gateway/
RUN dotnet pack BuildingBlocks.slnx -c Release -o artifacts/packages
RUN dotnet publish src/Gateway/Social.Gateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Social.Gateway.dll"]
