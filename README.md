# DashyNMS MCP

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server for LibreNMS, so Claude Desktop, Claude Code or any other MCP client can answer questions about your network: what a device's health is, which alerts are open, what changed on a switch in the last day.

It reuses the LibreNMS, Unimus and Graylog clients from [DashyNMS desktop](https://github.com/DashyNMS/desktop)'s Core library.

> **Status:** proof of concept. The project builds, but no tools are available yet.

## Building

You need the .NET 10 SDK.

```bash
git clone --recurse-submodules https://github.com/DashyNMS/mcp.git
cd mcp
dotnet build DashyNMS.Mcp.slnx
dotnet test tests/DashyNMS.Agent.Tests
```

If you cloned without `--recurse-submodules`, run `git submodule update --init` first.

## Security

The server only reads. Give it the API token of a **read-only LibreNMS user**, so LibreNMS itself refuses any change even if something goes wrong. See [SECURITY.md](SECURITY.md) to report a problem.

## Licence

See [LICENSE](LICENSE).
