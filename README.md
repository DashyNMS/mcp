# DashyNMS MCP

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server for LibreNMS, so Claude Desktop, Claude Code or any other MCP client can answer questions about your network: what a device's health is, which alerts are open, what changed on a switch in the last day.

It reuses the LibreNMS, Unimus and Graylog clients from [DashyNMS desktop](https://github.com/DashyNMS/desktop)'s Core library.

> **Status:** proof of concept.

## Tools

Every tool only reads. Each takes a device as a hostname, display name, IP address or LibreNMS device ID.

| Tool | Answers |
| --- | --- |
| `find_device` | Which devices match a name, IP or MAC address (MACs through LibreNMS's ARP tables) |
| `device_health` | Is a device OK: status, uptime, maintenance, open alerts, CPU, memory and storage peaks, problem ports |
| `port_status` | One port: state, speed, traffic, errors, alias and its LLDP/CDP neighbour |
| `active_alerts` | What's alerting now, grouped by device, optionally by severity or device |
| `device_events` | A device's event log, alert changes and outages for the last 1 to 168 hours |
| `what_changed` | The same timeline, plus the Unimus config diff and Graylog messages when those are configured |

Results are JSON. Secrets (SNMP communities, passwords, keys, private keys and pfSense secrets) are redacted before anything leaves the server. Results are capped at `MCP_MAX_RESULT_BYTES`, and a cut result says so.

## Building

You need the .NET 10 SDK.

```bash
git clone --recurse-submodules https://github.com/DashyNMS/mcp.git
cd mcp
dotnet build DashyNMS.Mcp.slnx
dotnet test DashyNMS.Mcp.slnx
```

If you cloned without `--recurse-submodules`, run `git submodule update --init` first.

For an MCP client, publish the server once and point the client at the published program:

```bash
dotnet publish src/DashyNMS.Mcp -c Release -o publish
```

Don't use `dotnet run` in a client's configuration. It can write build output to stdout, which the server uses for the MCP protocol.

## Using it with Claude

Claude Code (on Windows the program is `publish\dashynms-mcp.exe`; use its full path):

```bash
claude mcp add dashynms -e LIBRENMS_URL=https://librenms.example.com -e LIBRENMS_TOKEN=your-token -- /path/to/mcp/publish/dashynms-mcp
```

Claude Desktop, in `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "dashynms": {
      "command": "C:\\path\\to\\mcp\\publish\\dashynms-mcp.exe",
      "env": {
        "LIBRENMS_URL": "https://librenms.example.com",
        "LIBRENMS_TOKEN": "your-token"
      }
    }
  }
}
```

## Docker

The image is about 45 MB to pull and runs as a non-root user on Ubuntu's chiseled .NET runtime image, which has no shell or package manager. It is built for amd64 and arm64 and published to GitHub's container registry:

| Tag | What it is |
| --- | --- |
| `ghcr.io/dashynms/mcp:edge` | The latest build from `main` |
| `ghcr.io/dashynms/mcp:1.2.3`, `:1.2`, `:latest` | A release, from a `v1.2.3` tag |
| `ghcr.io/dashynms/mcp:sha-abc1234` | One exact commit |

```bash
docker pull ghcr.io/dashynms/mcp:edge
```

Or build it yourself from the repo root, with the submodule checked out:

```bash
docker build -t dashynms-mcp .
```

The examples below use the local name `dashynms-mcp`; put `ghcr.io/dashynms/mcp:edge` in its place to use the published image.

The server talks MCP over stdin and stdout, so the client starts the container itself and `-i` is required. A bare `-e NAME` passes that variable through from the client's environment, so the token stays out of the command line:

```bash
claude mcp add dashynms -e LIBRENMS_URL=https://librenms.example.com -e LIBRENMS_TOKEN=your-token -- docker run -i --rm -e LIBRENMS_URL -e LIBRENMS_TOKEN dashynms-mcp
```

Or keep the settings in an env file outside the repo (`NAME=value` per line, no quotes) and use `docker run -i --rm --env-file C:\path\to\dashynms.env dashynms-mcp`. In Claude Desktop, `command` is `docker` and `args` are the same words.

The image doesn't include time zone data, so leave `LIBRENMS_TIMESTAMPS_UTC` at `true` when using Docker. That's LibreNMS's usual setup.

## Configuration

The server is configured only through environment variables. It checks them, then tests the LibreNMS address and token, before it accepts any MCP traffic. If anything is wrong it stops with a plain message on stderr.

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `LIBRENMS_URL` | Yes | | Address of your LibreNMS server. The API URL (`.../api/v0`) works too. |
| `LIBRENMS_TOKEN` | Yes | | API token of a read-only LibreNMS user |
| `LIBRENMS_BACKUP_URL` | No | | Another address for the same server, used when the main one doesn't answer |
| `LIBRENMS_TRUSTED_CERTS` | No | | Comma-separated SHA-256 fingerprints of certificates to trust when the normal checks reject them (self-signed or internal CA) |
| `LIBRENMS_TIMEOUT` | No | `30` | Request timeout in seconds (at least 5), for every source |
| `LIBRENMS_TIMESTAMPS_UTC` | No | `true` | Whether LibreNMS stores timestamps in UTC. Set `false` if it stores local time, and run the server in the same time zone. |
| `UNIMUS_URL`, `UNIMUS_TOKEN` | No | | Turn on Unimus config diffs in `what_changed`. Set both or neither. |
| `UNIMUS_TRUSTED_CERTS` | No | | As `LIBRENMS_TRUSTED_CERTS`, for Unimus |
| `GRAYLOG_URL`, `GRAYLOG_USERNAME`, `GRAYLOG_PASSWORD` | No | | Turn on Graylog messages in `what_changed`. With an access token, the token is the username and the password is `token`. |
| `GRAYLOG_VERSION` | No | `2.1` | `2.1` for Graylog 2.1 or newer, or `2.0` |
| `GRAYLOG_QUERY_FIELD` | No | `source` | The Graylog field matched against a device's addresses, as LibreNMS's `graylog.query_field` |
| `GRAYLOG_TRUSTED_CERTS` | No | | As `LIBRENMS_TRUSTED_CERTS`, for Graylog |
| `MCP_MAX_RESULT_BYTES` | No | `16384` | The most JSON one tool call returns |

Unimus and Graylog aren't tested at startup. If one can't be reached, `what_changed` reports that source's error and still returns the rest.

### Self-signed and internal CA certificates

The server can't ask you whether to trust a certificate. If your LibreNMS certificate doesn't pass the normal checks, startup fails and prints the certificate's subject, issuer, validity dates and fingerprint. Check the fingerprint against the server's own certificate, then add it to `LIBRENMS_TRUSTED_CERTS`. Only that exact certificate is trusted. When it is renewed, startup fails again and prints the new fingerprint. There is no setting that trusts every certificate.

## Security

The server only reads. Give it the API token of a **read-only LibreNMS user**, so LibreNMS itself refuses any change even if something goes wrong. Use a Unimus token and a Graylog user that can only read, too.

Everything a tool returns from your network (device names, port aliases, log lines, config text) is marked as data, not instructions, but it still goes to the model you connect. Unimus config diffs are redacted, yet still contain your configuration. Leave Unimus unset if that shouldn't leave your network. See [SECURITY.md](SECURITY.md) to report a problem.

## Licence

See [LICENSE](LICENSE).
