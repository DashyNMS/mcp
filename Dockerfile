# DashyNMS MCP server. Speaks MCP over stdio, so run it with -i:
#   docker run -i --rm -e LIBRENMS_URL=... -e LIBRENMS_TOKEN=... dashynms-mcp
#
# Build from the repo root with the desktop submodule checked out:
#   git submodule update --init && docker build -t dashynms-mcp .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore on its own layer, so a source change doesn't download packages again.
COPY global.json Directory.Build.props ./
COPY src/DesktopNMS.Core.Portable/DesktopNMS.Core.Portable.csproj src/DesktopNMS.Core.Portable/
COPY src/DashyNMS.Agent/DashyNMS.Agent.csproj src/DashyNMS.Agent/
COPY src/DashyNMS.Mcp/DashyNMS.Mcp.csproj src/DashyNMS.Mcp/
RUN dotnet restore src/DashyNMS.Mcp

# Only Core's sources from the submodule: the server compiles them itself
# (see DesktopNMS.Core.Portable), and nothing else from desktop is needed.
COPY external/desktop/src/DesktopNMS.Core/ external/desktop/src/DesktopNMS.Core/
COPY src/ src/

# Not trimmed: the MCP SDK finds tools, and System.Text.Json writes results,
# by reflection. Invariant globalisation because the chiseled image has no
# ICU, and the server formats nothing by culture.
RUN dotnet publish src/DashyNMS.Mcp -c Release --no-restore -o /app \
    -p:UseAppHost=false \
    -p:InvariantGlobalization=true \
    -p:DebugType=none

# Ubuntu chiseled: the .NET runtime and CA certificates, no shell or package
# manager, and a non-root user by default.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "dashynms-mcp.dll"]
