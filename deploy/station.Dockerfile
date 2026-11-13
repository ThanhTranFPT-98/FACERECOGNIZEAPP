# Station software (DSAVehicle) for the laptop lab. Build context: the DSA_Vehicles repo root.
#   docker build -f deploy/station.Dockerfile -t dsa-station:lab .
# The UI is a separate image (deploy/ui.Dockerfile); this one serves the API, the hubs and the receiver.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY DSAVehicle/DSAVehicle.csproj DSAVehicle/
RUN dotnet restore DSAVehicle/DSAVehicle.csproj
COPY DSAVehicle/ DSAVehicle/
RUN dotnet publish DSAVehicle/DSAVehicle.csproj -c Release -o /app/publish /p:UseAppHost=false

# Debian base, not Alpine: the station formats times in Asia/Ho_Chi_Minh and needs the tz database.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl openssl tzdata ca-certificates \
 && rm -rf /var/lib/apt/lists/*
ENV TZ=Asia/Ho_Chi_Minh \
    ASPNETCORE_URLS=http://0.0.0.0:5085 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    STATION_DATA=/data
WORKDIR /app
COPY --from=build /app/publish ./
COPY deploy/station-entrypoint.sh /usr/local/bin/station-entrypoint
RUN chmod +x /usr/local/bin/station-entrypoint && mkdir -p /data
VOLUME ["/data"]
EXPOSE 5085
HEALTHCHECK --interval=15s --timeout=5s --start-period=90s --retries=5 \
  CMD curl -fsS http://localhost:5085/health || exit 1
ENTRYPOINT ["/usr/local/bin/station-entrypoint"]
