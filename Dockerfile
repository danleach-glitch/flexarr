# syntax=docker/dockerfile:1

# Base images are pinned by digest (the multi-platform index), so a build gets exactly the image reviewed, and
# Dependabot (.github/dependabot.yml) proposes new digests. To update one by hand:
#   docker buildx imagetools inspect node:24-alpine    (the "Digest:" line)

# ---- Web client ----
FROM --platform=$BUILDPLATFORM node:24-alpine@sha256:06d7adf3e0ef61a13f1d28682726366f97a15aa1888c934d3ecec86c33918b53 AS web
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- Server ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
ARG TARGETARCH
ARG VERSION=0.1.0
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig CHANGELOG.md ./
COPY src/ src/
# Docker says amd64, .NET says x64.
RUN arch=$([ "$TARGETARCH" = "amd64" ] && echo x64 || echo "$TARGETARCH") \
    && dotnet publish src/Flexarr.Api -c Release -a "$arch" --no-self-contained -o /app -p:Version=$VERSION

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
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
