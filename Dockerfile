# syntax=docker/dockerfile:1

# ---- Web client ----
FROM --platform=$BUILDPLATFORM node:25-alpine AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- Server ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.1.0
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
# Docker says amd64, .NET says x64.
RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
    && dotnet publish src/Flexarr.Api -c Release -a "$arch" --no-self-contained -o /app -p:Version=$VERSION

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /src/frontend/dist ./wwwroot

ENV Flexarr__DataDirectory=/config \
    ASPNETCORE_HTTP_PORTS=8383
RUN mkdir /config && chown $APP_UID /config
VOLUME /config
EXPOSE 8383
USER $APP_UID
ENTRYPOINT ["dotnet", "Flexarr.dll"]
