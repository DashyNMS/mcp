# syntax=docker/dockerfile:1
# DashyNMS MCP server. Speaks MCP over stdio, so run it with -i:
#   docker run -i --rm -e LIBRENMS_URL=... -e LIBRENMS_TOKEN=... dashynms-mcp
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
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
ARG TARGETARCH
WORKDIR /src

# Restore on its own layer, so a source change doesn't download packages again.
COPY global.json Directory.Build.props nuget.config ./
COPY src/DashyNMS.Agent/DashyNMS.Agent.csproj src/DashyNMS.Agent/
COPY src/DashyNMS.Mcp/DashyNMS.Mcp.csproj src/DashyNMS.Mcp/
RUN --mount=type=secret,id=packages,env=DASHYNMS_PACKAGES_TOKEN \
    dotnet restore src/DashyNMS.Mcp -a $TARGETARCH

COPY src/ src/

# Not trimmed: the MCP SDK finds tools, and System.Text.Json writes results,
# by reflection. Invariant globalisation because the chiseled image has no
# ICU, and the server formats nothing by culture.
RUN dotnet publish src/DashyNMS.Mcp -c Release --no-restore -a $TARGETARCH --self-contained false -o /app \
    -p:UseAppHost=false \
    -p:InvariantGlobalization=true \
    -p:DebugType=none

# Ubuntu chiseled: the .NET runtime and CA certificates, no shell or package
# manager, and a non-root user by default.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled@sha256:3eb54ac708855f74b032de48e02437d15fb083b2aa593d49fdd512bb17f3e944
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "dashynms-mcp.dll"]
