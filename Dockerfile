# syntax=docker/dockerfile:1.27.1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e

# Base images are pinned by digest (the multi-platform index), so a build gets exactly the image reviewed, and
# Dependabot (.github/dependabot.yml) proposes new digests. To update one by hand:
#   docker buildx imagetools inspect node:24-alpine    (the "Digest:" line)
# The syntax line above (the BuildKit frontend that reads this file) is pinned the same way, but Dependabot doesn't
# update it: check docker/dockerfile's releases now and then and update it with
#   docker buildx imagetools inspect docker/dockerfile:1

# ---- Web client ----
FROM --platform=$BUILDPLATFORM node:24-alpine@sha256:06d7adf3e0ef61a13f1d28682726366f97a15aa1888c934d3ecec86c33918b53 AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- Server ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
# Set by CI for a tagged release; otherwise the version in Directory.Build.props is used.
ARG VERSION
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig CHANGELOG.md ./
COPY src/ src/
# Docker says amd64, .NET says x64.
RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
    && dotnet publish src/Flexarr.Api -c Release -a "$arch" --no-self-contained -o /app ${VERSION:+-p:Version=$VERSION}

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
ARG TARGETARCH
# The VA-API drivers, so graphics passed in with --device /dev/dri can convert video: Mesa's for AMD everywhere, and
# Intel's (VA-API and Quick Sync, old chips and new) on amd64, the only place Intel ships them.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg mesa-va-drivers \
    && if [ "$TARGETARCH" = "amd64" ]; then \
        apt-get install -y --no-install-recommends intel-media-va-driver-non-free i965-va-driver libmfx-gen1.2 libmfx1; \
    fi \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /src/frontend/dist ./wwwroot
COPY deploy/docker/entrypoint.sh /usr/local/bin/flexarr-entrypoint

ENV Flexarr__DataDirectory=/config \
    ASPNETCORE_HTTP_PORTS=8383
RUN mkdir /config && chown $APP_UID:$APP_UID /config
VOLUME /config
EXPOSE 8383
# Lets Docker, Compose and NAS apps see a server that's stopped answering. The image has no curl, so bash asks /health
# itself, on the port the server listens on (the first, if several are set).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["bash", "-c", "port=${ASPNETCORE_HTTP_PORTS%%[;,]*}; exec 3<>/dev/tcp/127.0.0.1/${port:-8383} && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
# Starts as root only to hand /config to PUID:PGID (1654 unless set), then runs Flexarr as that user. --user skips it.
ENTRYPOINT ["flexarr-entrypoint"]
