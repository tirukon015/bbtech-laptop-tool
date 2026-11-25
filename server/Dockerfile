# BBTech API container. Build from the repository root:
#   docker build -f server/Dockerfile -t bbtech-api .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY server/BBTech.Api/ server/BBTech.Api/
COPY Directory.Build.props ./
RUN dotnet publish server/BBTech.Api/BBTech.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Connection string and TLS are supplied at runtime (ConnectionStrings__Postgres, reverse proxy / Kestrel certificate).
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "BBTech.Api.dll"]
