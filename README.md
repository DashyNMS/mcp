<p align="center">
  <img src="https://dashynms.pckp.net/assets/img/favicon.svg" alt="" width="96" height="96">
</p>

<h1 align="center">DashyNMS MCP</h1>

<p align="center">
  <strong>Ask Claude about your LibreNMS network.</strong><br>
  For Claude Desktop, Claude Code and any other MCP client.
</p>

<p align="center">
  <a href="https://github.com/DashyNMS/mcp/pkgs/container/mcp">Container image</a> ·
  <a href="https://github.com/DashyNMS/mcp/issues">Issues</a> ·
  <a href="https://github.com/DashyNMS/mcp/milestones">Roadmap</a>
</p>

---

"What's alerting right now?" "How is the core switch doing?" "What changed on this firewall since yesterday?" DashyNMS MCP lets an AI assistant answer questions like these from your own [LibreNMS](https://www.librenms.org/) server, with the evidence beside every answer, and without being able to change a thing.

It's a [Model Context Protocol](https://modelcontextprotocol.io) server, built on the same LibreNMS, Unimus and Graylog code as [DashyNMS desktop](https://github.com/DashyNMS/desktop) and [DashyNMS Mobile](https://github.com/DashyNMS/mobile).

> **Status:** early. It works end to end, and the [v1.1.0 milestone](https://github.com/DashyNMS/mcp/milestones) tracks what comes next.

## What you can ask

**Find anything**
By part of a name, an IP address, or a MAC address, or list devices by status, device group or location: "which devices are down at the north site?". A MAC is looked up in LibreNMS's ARP tables, so you find the router or switch that saw it. *(`find_device`, `list_groups_and_locations`)*

**Is it OK?**
One answer per device: up or down, uptime, maintenance, open alerts, CPU, memory, the fullest storage, and the ports that are down or counting errors. *(`device_health`)*

**What's on that port?**
State, speed, VLANs, traffic, errors, its description, and the neighbour LibreNMS sees on it over LLDP or CDP. *(`port_status`)*

**Everything else about a device**
Its inventory with models and serials, VLANs with their tagged and untagged ports, the MAC and ARP tables, PoE budget, wireless, BGP and OSPF, availability over the last day to year, and its own IP addresses. Ask for just the parts you need. *(`device_details`)*

**Is anything running hot?**
Temperatures, fans, optical power, voltages and more, for one device or across the network, worst first, judged the same way as DashyNMS's Health tab. *(`device_sensors`, `health_overview`)*

**What's it connected to?**
Every LLDP and CDP neighbour of a device, including ones that report it when it doesn't report them back. *(`device_neighbours`)*

**How's the network?**
One call for the dashboard's numbers: devices up and down, where the outages are, alerts by severity, ports down or counting errors. Then the busiest ports, the ports counting the most errors, and the devices with the most traffic or the most port problems. *(`network_overview`, `top_interfaces`, `top_errors`, `top_devices`)*

**What happened across the network?**
The event log and alert log for every device over any window, newest first: "anything reboot today?". Filter by type, severity, device, group or location. *(`network_events`)*

**What's alerting, and what alerted?**
Every open alert, grouped by device and newest first, or every alert raised in a window of time, including ones that have since recovered: "what fired overnight?". Narrow it to a severity, a device, a device group or a location. *(`active_alerts`)*

**What's configured, and what changed?**
With Unimus connected: a device's config backup, newest or any earlier one, a diff between any two, and every device whose config changed in a window, across the network. Secrets are redacted first, and you can choose to send no config text at all. *(`config_backup`, `config_diff`, `recent_config_changes`)*

**Why did it fire, and what are we alerting on?**
For any alert: what the rule matched and the values that tripped it, the rule's condition and procedure, and how often it has fired on that device in the last 30 days, to tell a one-off from a flapping alert. For the rules themselves: what each watches for, what it applies to, its template, and which are noisiest. *(`alert_details`, `alert_rules`)*

**What happened, and what changed?**
A device's event log, alert changes and outages for any window: "the last 6 hours", "since Friday", or "between 2 and 4 last night". With Unimus and Graylog connected, you also get the config diff for a change inside that window and the messages the device sent. *(`device_events`, `what_changed`)*

Each tool takes a device however you'd name it: hostname, display name, IP address or LibreNMS ID. If more than one device matches, the assistant is told which ones, and asks you. Long lists come back a page at a time, so a broad question still gets a complete answer. A window can reach back up to a month, and when LibreNMS's logs don't go back that far, the answer says so.

## Safe by design

- **It only reads.** There are no tools that change anything. Give it the token of a read-only LibreNMS user too, so LibreNMS itself would refuse a change. Do the same for Unimus and Graylog.
- **Secrets stay out of answers.** SNMP communities, passwords and hashes, RADIUS, TACACS, IPsec, NTP and OSPF keys, API keys, private keys and pfSense secrets are redacted before anything leaves the server.
- **Your network's text is treated as data.** Device names, port descriptions, log lines and config text never appear as the server's own words, and the assistant is told not to take instructions from them.
- **Answers stay small.** Every result is capped (16 KB by default), and says when something was left out.
- **Tokens stay on your side.** They're read from the environment, never logged, and never sent anywhere but the server they're for.

What the tools return still goes to whichever model your client uses. Config backups and diffs are redacted, but they're still your configuration. If that shouldn't leave your network, set `MCP_CONFIG_TEXT=summary`, which keeps the answers to "did it change, when, and by how much?" without any config text, or `off`.

## Get it

The easiest way is the container image. It's about 45 MB to pull, built for amd64 and arm64, runs as a non-root user and has no shell:

```bash
docker pull ghcr.io/dashynms/mcp:edge
```

| Tag | What it is |
| --- | --- |
| `edge` | The latest build from `main` |
| `latest`, `1.2.3`, `1.2` | A release |
| `sha-abc1234` | One exact commit |

You can also build it yourself from source; see the [developer guide](docs/DEVELOPMENT.md).

## What you need

- A LibreNMS server you can reach, with its API turned on.
- An API token for a **read-only** LibreNMS user. In LibreNMS, open the settings menu, then **API → API Tokens**, and create one for that user.
- Docker, or the .NET 10 SDK to build from source.
- Optionally, Unimus (1.7 or later) and Graylog, for `what_changed`.

LibreNMS only gives its network-wide sensor list to users with **global read** access. Without it, `device_sensors` still works, reading one device at a time, and `health_overview` works when it's narrowed to a device group or location of up to 25 devices.

## Set it up

The server talks to the client over stdin and stdout, so the client starts it, and Docker needs `-i`.

**Claude Code**: a bare `-e NAME` hands the value through from Claude Code, so the token stays out of the Docker command:

```bash
claude mcp add dashynms -e LIBRENMS_URL=https://librenms.example.com -e LIBRENMS_TOKEN=your-token -- docker run -i --rm -e LIBRENMS_URL -e LIBRENMS_TOKEN ghcr.io/dashynms/mcp:edge
```

**Claude Desktop**: put your settings in an env file outside any repo (one `NAME=value` per line, no quotes), then add this to `claude_desktop_config.json` through **Settings → Developer → Edit Config**:

```json
{
  "mcpServers": {
    "dashynms": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "--env-file", "C:\\path\\to\\dashynms.env", "ghcr.io/dashynms/mcp:edge"]
    }
  }
}
```

Quit Claude Desktop completely (from the tray icon, not just the window) and open it again. It reads the file only at startup, and while it's running it can write over changes you make to the file.

When the server starts, it checks its settings and tests your LibreNMS address and token before it answers anything. If something's wrong, it stops with one plain line on stderr. Claude Desktop shows that line under **Settings → Developer**.

## Settings

All settings are environment variables.

| Variable | Required | Default | What it's for |
| --- | --- | --- | --- |
| `LIBRENMS_URL` | Yes | | Your LibreNMS address. The API URL (`.../api/v0`) works too. |
| `LIBRENMS_TOKEN` | Yes | | A read-only user's API token |
| `LIBRENMS_BACKUP_URL` | No | | Another address for the same server, used when the main one doesn't answer |
| `LIBRENMS_TRUSTED_CERTS` | No | | SHA-256 fingerprints of certificates to trust when the normal checks reject them, comma-separated |
| `LIBRENMS_TIMEOUT` | No | `30` | Request timeout in seconds, at least 5, for every source |
| `LIBRENMS_TIMESTAMPS_UTC` | No | `true` | Whether LibreNMS stores times in UTC. Leave it on with Docker, as the image has no time zone data. |
| `UNIMUS_URL`, `UNIMUS_TOKEN` | No | | Turn on the config tools and config diffs in `what_changed`. Set both or neither. |
| `MCP_CONFIG_TEXT` | No | `full` | How much config the tools return: `full` (redacted text), `summary` (when it changed and how many lines, no text), or `off` (nothing from Unimus) |
| `UNIMUS_TRUSTED_CERTS` | No | | As `LIBRENMS_TRUSTED_CERTS`, for Unimus |
| `GRAYLOG_URL`, `GRAYLOG_USERNAME`, `GRAYLOG_PASSWORD` | No | | Turn on Graylog messages in `what_changed`. With an access token, the token is the username and the password is `token`. |
| `GRAYLOG_VERSION` | No | `2.1` | `2.1` for Graylog 2.1 or newer, or `2.0` |
| `GRAYLOG_QUERY_FIELD` | No | `source` | The Graylog field matched against a device's addresses, as LibreNMS's `graylog.query_field` |
| `GRAYLOG_TRUSTED_CERTS` | No | | As `LIBRENMS_TRUSTED_CERTS`, for Graylog |
| `MCP_MAX_RESULT_BYTES` | No | `16384` | The most JSON one tool call returns |

Unimus and Graylog aren't tested at startup. If one can't be reached, `what_changed` says so and still returns everything else.

**Self-signed or internal CA certificates:** the server can't ask whether to trust a certificate, so if yours doesn't pass the normal checks, startup stops. It prints the certificate's subject, issuer, dates and fingerprint. Check the fingerprint against your server's own certificate, then add it to `LIBRENMS_TRUSTED_CERTS`. Only that exact certificate is trusted, so when it's renewed you add the new one. There's no setting that trusts every certificate.

## Help and feedback

- Found a bug or have an idea? [Open an issue](https://github.com/DashyNMS/mcp/issues). Planned work is tracked there too, under the [milestones](https://github.com/DashyNMS/mcp/milestones).

---

<sub>DashyNMS isn't affiliated with LibreNMS, Graylog, Unimus or Anthropic. LibreNMS, Graylog, Unimus and Claude are trademarks of their respective owners.<br>
Source-available under the PolyForm Internal Use License 1.0.0: free to build, run and change for your own organisation's internal use, but not to distribute or offer to others; see [LICENSE](LICENSE).<br>
Found a security problem? See [SECURITY.md](SECURITY.md). Technical notes are in the [developer guide](docs/DEVELOPMENT.md).</sub>
