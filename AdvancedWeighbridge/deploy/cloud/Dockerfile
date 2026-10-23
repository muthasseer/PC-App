# AWMS Cloud server image. Build from the AdvancedWeighbridge folder:
#   docker build -f deploy/cloud/Dockerfile -t awms-cloud:1.0.0 .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/Weighbridge.Cloud/Weighbridge.Cloud.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Data folder for the generated key file (and the database when SQLite is used); owned by the non-root app user.
RUN mkdir -p /data && chown "$APP_UID" /data
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Cloud__DataDirectory=/data
USER $APP_UID
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Weighbridge.Cloud.dll"]
