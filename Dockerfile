# BAMF in a container. Build from the repo root:
#
#   docker build -t bamf .
#
# Run on the host's network, since ARP only works on the network the process
# is actually on, with NET_RAW for the active ARP scan and the traffic monitor:
#
#   docker run -d --name bamf --network host --cap-add NET_RAW --cap-add NET_ADMIN \
#     -v bamf-data:/data -e Bamf__Subnets__0=192.168.1.0/24 ghcr.io/rhc52980/bamf
#
# Every setting in appsettings.json can be given as an environment variable
# with __ for the colon (Bamf__ScanIntervalSeconds=30), or mount your own
# appsettings.json over /app/appsettings.json. The database lives in /data.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BAMF/BAMF.csproj BAMF/
RUN dotnet restore BAMF/BAMF.csproj
COPY BAMF/ BAMF/
RUN dotnet publish BAMF/BAMF.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# libpcap for the active ARP scan and the traffic monitor; ca-certificates for
# the OUI download and webhooks over HTTPS; curl for the health check.
RUN apt-get update -qq && apt-get install -y -qq --no-install-recommends libpcap0.8 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /data
VOLUME /data
ENV Urls=http://0.0.0.0:8840 \
    Bamf__DatabasePath=/data/bamf.db \
    DOTNET_EnableDiagnostics=0
EXPOSE 8840
HEALTHCHECK --interval=60s --timeout=5s --start-period=30s CMD curl -fsS http://127.0.0.1:8840/api/hosts >/dev/null || exit 1
ENTRYPOINT ["dotnet", "BAMF.dll"]
