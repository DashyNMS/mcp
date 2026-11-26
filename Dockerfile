# syntax=docker/dockerfile:1
# DashyNMS MCP server. Speaks MCP over stdio, so run it with -i:
#   docker run -i --rm -e LIBRENMS_URL=... -e LIBRENMS_TOKEN=... dashynms-mcp
#
# Or over HTTP, behind a reverse proxy that does TLS:
#   docker run -d -p 127.0.0.1:8080:8080 -e MCP_TRANSPORT=http -e MCP_HTTP_TOKENS=... \
#     -e LIBRENMS_URL=... -e LIBRENMS_TOKEN=... dashynms-mcp
#
# Build from the repo root. Core comes from GitHub Packages, which needs a
# token with read:packages, passed as a build secret so it never lands in a layer:
#   DASHYNMS_PACKAGES_TOKEN=... docker build --secret id=packages,env=DASHYNMS_PACKAGES_TOKEN -t dashynms-mcp .

# The SDK runs on the build machine's own architecture and cross-compiles for
# the target (TARGETARCH, set by buildx), so an arm64 image builds as fast as
# an amd64 one, with no emulation.
#
# Base images are pinned by digest (the multi-arch index, so every platform
# still resolves) and kept current by Dependabot.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
ARG TARGETARCH
WORKDIR /src
# Build-stage legal evidence collector; cached independently of source.
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*

# Restore on its own layer, so a source change doesn't download packages again.
COPY global.json Directory.Build.props nuget.config ./
COPY src/DashyNMS.Agent.Shared/DashyNMS.Agent.Shared.csproj src/DashyNMS.Agent.Shared/
COPY src/DashyNMS.Agent/DashyNMS.Agent.csproj src/DashyNMS.Agent/
COPY src/DashyNMS.Mcp/DashyNMS.Mcp.csproj src/DashyNMS.Mcp/
RUN --mount=type=secret,id=packages,env=DASHYNMS_PACKAGES_TOKEN \
    dotnet restore src/DashyNMS.Mcp -a $TARGETARCH

COPY src/ src/
# The server's own user guide, which the help tool reads.
COPY docs/guide/ docs/guide/
COPY scripts/collect-dependency-notices.py scripts/
COPY docs/dependency-licences/ docs/dependency-licences/
# Collect legal files from the exact restored packages, including transitives.
# Python is a build-stage tool only; it is not copied into the runtime image.
RUN python3 scripts/collect-dependency-notices.py src/DashyNMS.Mcp/obj/project.assets.json /third-party docs/dependency-licences

# Not trimmed: the MCP SDK finds tools, and System.Text.Json writes results,
# by reflection. Invariant globalisation because the chiseled image has no
# ICU, and the server formats nothing by culture.
# VERSION is what the server reports; a build without one is the version being
# built towards.
ARG VERSION=
RUN dotnet publish src/DashyNMS.Mcp -c Release --no-restore -a $TARGETARCH --self-contained false -o /app \
    ${VERSION:+-p:Version=$VERSION} \
    -p:UseAppHost=false \
    -p:InvariantGlobalization=true \
    -p:DebugType=none

# Owned by the runtime's user, so a volume mounted here can be written to
# (MCP_LAST_KNOWN_GOOD_DIR).
RUN mkdir /data

# Ubuntu chiseled: the .NET and ASP.NET Core runtimes (the HTTP transport) and
# CA certificates, no shell or package manager, and a non-root user by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:bcdfe6b8e923ad3d05f01dfa805ab97c99d742823ce030856002a6db38122712
WORKDIR /app
LABEL org.opencontainers.image.licenses="MIT" \
      net.dashynms.license.scope="MIT applies to DashyNMS project-owned material only; dependencies and base layers retain their own licences. See /app/NOTICES.md."
COPY --from=build /app .
COPY --from=build --chown=1654:1654 /data /data
COPY LICENSE NOTICES.md ./
COPY --from=build /third-party ./third-party

# Only used with MCP_TRANSPORT=http. Every address inside the container; what
# reaches it is up to the ports the container publishes.
ENV MCP_HTTP_ADDRESS=*
EXPOSE 8080

ENTRYPOINT ["dotnet", "dashynms-mcp.dll"]
