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

## Configuration

The server is configured only through environment variables. It checks them, then tests the LibreNMS address and token, before it accepts any MCP traffic. If anything is wrong it stops with a plain message on stderr.

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `LIBRENMS_URL` | Yes | | Address of your LibreNMS server. The API URL (`.../api/v0`) works too. |
| `LIBRENMS_TOKEN` | Yes | | API token of a read-only LibreNMS user |
| `LIBRENMS_BACKUP_URL` | No | | Another address for the same server, used when the main one doesn't answer |
| `LIBRENMS_TRUSTED_CERTS` | No | | Comma-separated SHA-256 fingerprints of certificates to trust when the normal checks reject them (self-signed or internal CA) |
| `LIBRENMS_TIMEOUT` | No | `30` | Request timeout in seconds (at least 5) |
| `MCP_MAX_RESULT_BYTES` | No | `16384` | The most JSON one tool call returns |

### Self-signed and internal CA certificates

The server can't ask you whether to trust a certificate. If your LibreNMS certificate doesn't pass the normal checks, startup fails and prints the certificate's subject, issuer, validity dates and fingerprint. Check the fingerprint against the server's own certificate, then add it to `LIBRENMS_TRUSTED_CERTS`. Only that exact certificate is trusted. When it is renewed, startup fails again and prints the new fingerprint. There is no setting that trusts every certificate.

## Security

The server only reads. Give it the API token of a **read-only LibreNMS user**, so LibreNMS itself refuses any change even if something goes wrong. See [SECURITY.md](SECURITY.md) to report a problem.

## Licence

See [LICENSE](LICENSE).
