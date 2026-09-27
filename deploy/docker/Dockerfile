# BitaxeTuner-Server als Container (NAS, Home-Server, Raspberry Pi mit Docker)
# Multi-Arch: linux/amd64, linux/arm64, linux/arm/v7 – gebaut wird auf der Build-Plattform (schnell, kein QEMU für dotnet).
#
#   docker build -f deploy/docker/Dockerfile -t bitaxetuner-server .
#   docker run -d --name bitaxetuner -p 8484:8484 -v bitaxetuner-data:/data --restart unless-stopped bitaxetuner-server

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY Directory.Build.props ./
COPY src/BitaxeTuner.Core/ src/BitaxeTuner.Core/
COPY src/BitaxeTuner.Server/ src/BitaxeTuner.Server/
RUN dotnet publish src/BitaxeTuner.Server/BitaxeTuner.Server.csproj -c Release -a $TARGETARCH --self-contained false \
      -p:Version=$VERSION -p:DebugType=none -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
LABEL org.opencontainers.image.title="BitaxeTuner-Server" \
      org.opencontainers.image.description="24/7-Überwachung und Tuning für Bitaxe/NerdAxe-Miner mit Browser-Oberfläche" \
      org.opencontainers.image.source="https://github.com/Elemirus1996/BitaxeTuner"
ENV BITAXETUNER_DATA_DIR=/data \
    BITAXETUNER_PORT=8484 \
    TZ=Europe/Berlin
WORKDIR /app
COPY --from=build /app ./
RUN mkdir -p /data && chown $APP_UID /data
VOLUME /data
EXPOSE 8484
USER $APP_UID
ENTRYPOINT ["dotnet", "BitaxeTuner.Server.dll"]
