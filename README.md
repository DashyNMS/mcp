<p align="center">
  <img src="https://dashynms.pckp.net/assets/img/favicon.svg" alt="" width="96" height="96">
</p>

<h1 align="center">DashyNMS MCP</h1>

<p align="center">
  <strong>Ask Claude about your LibreNMS network.</strong><br>
  For Claude Desktop, Claude Code and any other MCP client.
</p>

<p align="center">
  <a href="https://dashynms.pckp.net/mcp/">Website</a> ·
  <a href="https://github.com/DashyNMS/mcp/pkgs/container/mcp">Container image</a> ·
  <a href="https://github.com/DashyNMS/mcp/releases/latest">Release notes</a> ·
  <a href="https://github.com/DashyNMS/mcp/milestones">Roadmap</a>
</p>

---

"What's alerting right now?" "How is the core switch doing?" "What changed on this firewall since yesterday?" DashyNMS MCP lets an AI assistant answer questions like these from your own [LibreNMS](https://www.librenms.org/) server, with the evidence beside every answer, and without being able to change a thing.

It's a [Model Context Protocol](https://modelcontextprotocol.io) server, built on the same LibreNMS, Unimus and Graylog code as [DashyNMS desktop](https://github.com/DashyNMS/desktop) and [DashyNMS Mobile](https://github.com/DashyNMS/mobile).

Its features match DashyNMS desktop and mobile 1.1, and later versions follow theirs; the [milestones](https://github.com/DashyNMS/mcp/milestones) show what's next.

## What you can ask

**Find anything**
By part of a name, an IP address, or a MAC address, or list devices by status, device group or location: "which devices are down at the north site?". A MAC is looked up in LibreNMS's ARP tables, so you find the router or switch that saw it. *(`find_device`, `list_groups_and_locations`)*

**Is it OK?**
One answer per device: up, down, disabled, ignored or not reported, uptime, maintenance, open alerts, CPU, memory, the fullest storage, and the ports that are down or counting errors. *(`device_health`)*

**What's on that port?**
State, speed, VLANs, traffic, errors, its description, and the neighbour LibreNMS sees on it over LLDP or CDP. *(`port_status`)*

**Everything else about a device**
Its inventory with models and serials, VLANs with their tagged and untagged ports, the MAC and ARP tables, PoE budget, wireless, BGP and OSPF, availability over the last day to year, and its own IP addresses. Ask for just the parts you need. *(`device_details`)*

**Is anything running hot?**
Temperatures, fans, optical power, voltages and more, for one device or across the network, worst first, judged the same way as DashyNMS's Health tab. *(`device_sensors`, `health_overview`)*

**What's it connected to?**
Every LLDP and CDP neighbour of a device, including ones that report it when it doesn't report them back. *(`device_neighbours`)*

**Where is this plugged in?**
From a MAC or IP address to the switch and edge port it's on, with the port's description, VLAN and state, and the switches in between. Ports that link to other switches are skipped, and if more than one port could be the answer it says so instead of guessing. *(`trace_endpoint`)*

**Show me the graph**
A device's, a port's or a single sensor's LibreNMS graph as an image in the chat: traffic, CPU, memory, ping, temperatures and the rest, or one temperature or optic on its own scale, over the last hour to the last year, or between any two times. *(`graph`)*

**How's the network?**
One call for the dashboard's numbers: devices up and down, where the outages are, alerts by severity, ports down or counting errors. Then the busiest ports, the ports counting the most errors, and the devices with the most traffic or the most port problems. *(`network_overview`, `top_interfaces`, `top_errors`, `top_devices`)*

**What happened across the network?**
The event log and alert log for every device over any window, newest first: "anything reboot today?". Filter by type, severity, device, group or location. *(`network_events`)*

**What did the logs say?**
With Graylog connected: its messages across the network or from one device, over any window up to 31 days, past or present: "any errors from the core switches overnight?". You get the exact count first and the messages over time, then the busiest sources, levels and applications and the newest lines. Narrow it by text, device, severity or stream. *(`search_logs`)*

**What happened to this device, and what changed?**
A device's event log, alert changes and outages for any window: "the last 6 hours", "since Friday", or "between 2 and 4 last night". With Unimus and Graylog connected, you also get the config diff for a change inside that window and the messages the device sent. *(`device_events`, `what_changed`)*

**What's alerting, and what alerted?**
Every open alert, grouped by device and newest first, or every alert raised in a window of time, including ones that have since recovered: "what fired overnight?". Narrow it to a severity, a device, a device group or a location. *(`active_alerts`)*

**Why did it fire, and what are we alerting on?**
For any alert, open or acknowledged: what the rule matched and the values that tripped it, the rule's condition and procedure, and how often it has fired on that device in the last 30 days, to tell a one-off from a flapping alert. For the rules themselves: what each watches for, what it applies to, its template, and which are noisiest. *(`alert_details`, `alert_rules`)*

**What's configured, and what changed?**
With Unimus connected: a device's config backup, newest or any earlier one, a diff between any two, and every device whose config changed in a window, across the network. Secrets are redacted first, you can choose to send no config text at all, and it tells you when a device's backups have stopped. *(`config_backup`, `config_diff`, `recent_config_changes`)*

**How do I do that in DashyNMS?**
How-to questions about DashyNMS Desktop, DashyNMS Mobile and this server, answered from their user guides, with the product and release each answer describes. It needs no LibreNMS. *(`dashynms_help`)*

**Where do I start?**
Three prompts in your client's prompt menu walk the assistant through the right tools in order: *Investigate this device*, *Morning check: what's down and why* and *What changed before this alert?* With a folder of your own runbooks, naming conventions or site notes ([`MCP_RESOURCES_DIR`](#your-own-reference-files)), your client can attach those too.

Each tool takes a device however you'd name it: hostname, display name, IP address or LibreNMS ID. If more than one device matches, the assistant is told which ones, and asks you. Long lists come back a page at a time, so a broad question still gets a complete answer. A window can reach back up to a month. On a busy network, filters such as an event type let the answer look further back through the logs, and when a window couldn't be read in full the answer says which part is missing.

## Safe by design

- **It only reads.** There are no tools that change anything. Give it the token of a read-only LibreNMS user too, so LibreNMS itself would refuse a change. Do the same for Unimus and Graylog.
- **Secrets stay out of answers.** SNMP communities, passwords and hashes, RADIUS, TACACS, IPsec, NTP and OSPF keys, API and licence keys, private keys, and pfSense, OpenWrt and Brocade secrets are redacted before anything leaves the server, including secrets inside base64-encoded config. The rules were checked by hand against real backups from eleven vendor types.
- **Your network's text is treated as data.** Device names, port descriptions, log lines and config text never appear as the server's own words, and the assistant is told not to take instructions from them.
- **Answers stay small.** Every result is capped (16 KB by default), and says when something was left out.
- **Answers have a fixed shape.** Every tool declares an output schema, and returns its result as structured content as well as the same JSON as text, so a client can rely on field names.
- **Every call is on record.** One line per tool call says who asked for what and how it went, with secrets redacted and nothing of the answer. See [the audit trail](#the-audit-trail).
- **You choose the tools.** Turn any of them off with `MCP_TOOLS_DENY`, or list the only ones to offer in `MCP_TOOLS_ALLOW`.
- **Your servers come first.** Requests to each of LibreNMS, Unimus and Graylog are capped (8 at once by default), however many questions are asked at once.
- **Tokens stay on your side.** They're read from the environment, never logged, and never sent anywhere but the server they're for. Use https:// for every service where you can: the server warns at startup about any on plain http, whose token or password would cross the network unencrypted.

What the tools return still goes to whichever model your client uses. Config backups and diffs are redacted, but they're still your configuration. If that shouldn't leave your network, set `MCP_CONFIG_TEXT=summary`, which keeps the answers to "did it change, when, and by how much?" without any config text, or `off`.

## Get it

The easiest way is the container image. It's about 45 MB to pull, built for amd64 and arm64, runs as a non-root user and has no shell:

```bash
docker pull ghcr.io/dashynms/mcp:latest
```

| Tag | What it is |
| --- | --- |
| `latest` | The newest release |
| `1.1`, `1.1.0` | A release line, or one exact release: pin these for no surprises |
| `nightly` | Last night's build of `main` |
| `edge` | The newest build of `main`, from every change; it may not work |
| `2.0.0-nightly.4`, `2.0.0-edge.12` | One exact build |
| `sha-abc1234` | One exact commit |

Docker keeps using the copy it already has, so to update, pull again and restart your client:

```bash
docker pull ghcr.io/dashynms/mcp:latest
```

**Without Docker**, from 2.0: each release, nightly and edge build on the [releases page](https://github.com/DashyNMS/mcp/releases) has one self-contained program per platform, with nothing else to install. Download the archive for yours, check it against `SHA256SUMS`, and unpack it anywhere:

| Platform | Archive |
| --- | --- |
| Windows | `dashynms-mcp-<version>-win-x64.zip`, `-win-arm64.zip` |
| macOS | `dashynms-mcp-<version>-osx-arm64.tar.gz` (Apple silicon), `-osx-x64.tar.gz` (Intel) |
| Linux | `dashynms-mcp-<version>-linux-x64.tar.gz`, `-linux-arm64.tar.gz` (glibc; on Alpine use the image) |

```bash
sha256sum --check --ignore-missing SHA256SUMS      # on macOS: shasum -a 256 --check --ignore-missing SHA256SUMS
gh attestation verify dashynms-mcp-*.tar.gz --repo DashyNMS/mcp   # optional: that it was built here
```

The macOS builds are signed and notarised. The Windows ones aren't signed yet, so SmartScreen may ask the first time. It reads its settings from the environment, and speaks stdio or HTTP exactly as the image does.

You can also build it yourself from source; see the [developer guide](docs/DEVELOPMENT.md).

## What you need

- A LibreNMS server you can reach, with its API turned on.
- An API token for a **read-only** LibreNMS user. In LibreNMS, open the settings menu, then **API → API Tokens**, and create one for that user.
- Docker, a [standalone program](#get-it), or the .NET 10 SDK to build from source.
- Optionally, Unimus (1.7 or later) and Graylog (2.1 or later, tested up to 7.2), for `what_changed` and `search_logs`.

**Give that user global read** for network-wide sensor questions ("is anything running hot?"). LibreNMS only gives its network-wide sensor list to users with its global read role or, on versions with custom roles, one with the `sensor.view` permission. Without it, `device_sensors` still works, and `health_overview` reads devices one by one: up to 25 in a device group or location in full, and a larger scope for about 20 seconds at a time, alerting devices first. The answer then says how many devices it covers, and asking again within a few minutes carries on from there. A new permission is noticed within 10 minutes.

## Set it up

The server talks to the client over stdin and stdout, so the client starts it, and Docker needs `-i`.

**Claude Code**: a bare `-e NAME` hands the value through from Claude Code, so the token stays out of the Docker command:

```bash
claude mcp add dashynms -e LIBRENMS_URL=https://librenms.example.com -e LIBRENMS_TOKEN=your-token -- docker run -i --rm -e LIBRENMS_URL -e LIBRENMS_TOKEN ghcr.io/dashynms/mcp:latest
```

**Claude Desktop**: put your settings in an env file outside any repo (one `NAME=value` per line, no quotes), then add this to `claude_desktop_config.json` through **Settings → Developer → Edit Config**:

```json
{
  "mcpServers": {
    "dashynms": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "--env-file", "C:\\path\\to\\dashynms.env", "ghcr.io/dashynms/mcp:latest"]
    }
  }
}
```

With a [standalone program](#get-it), `command` is its full path, there are no `args`, and the settings go in `env`:

```json
{
  "mcpServers": {
    "dashynms": {
      "command": "C:\\path\\to\\dashynms-mcp.exe",
      "env": { "LIBRENMS_URL": "https://librenms.example.com", "LIBRENMS_TOKEN": "your-token" }
    }
  }
}
```

Quit Claude Desktop completely (from the tray icon, not just the window) and open it again. It reads the file only at startup, and while it's running it can write over changes you make to the file.

To share one server between several people instead, [run it over HTTP](#share-it-over-http).

When the server starts, it checks its settings and tests your LibreNMS address and token before it answers anything. If something's wrong, it stops with one plain line on stderr. Claude Desktop shows that line under **Settings → Developer**.

## Settings

All settings are environment variables. The LibreNMS address and token are required, unless `MCP_TOOLS_ALLOW` is just `dashynms_help`, which needs neither.

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
| `UNIMUS_MY_DOMAIN` | No | | LibreNMS's discovery domain (its `mydomain` setting), such as `example.net`. Set it when LibreNMS knows devices by short names like `core-sw1` and Unimus knows them as `core-sw1.example.net`, so the config tools and `what_changed` can match them. Unset, matching is unchanged. A value that isn't a domain name stops the server at startup. |
| `GRAYLOG_URL`, `GRAYLOG_USERNAME`, `GRAYLOG_PASSWORD` | No | | Turn on `search_logs` and Graylog messages in `what_changed`. With an access token, the token is the username and the password is `token`. |
| `GRAYLOG_VERSION` | No | `2.1` | `2.1` for Graylog 2.1 and newer, including 6.x and 7.x (tested on 7.2). `2.0` only for Graylog 2.0. |
| `GRAYLOG_QUERY_FIELD` | No | `source` | The Graylog field matched against a device's addresses, as LibreNMS's `graylog.query_field` |
| `GRAYLOG_MATCH_ANY_ADDRESS` | No | `false` | Also match a device's messages on every address on its interfaces, as LibreNMS's and DashyNMS desktop's "match any address". Turn it on when devices log from a VLAN or loopback address rather than the one LibreNMS polls. |
| `GRAYLOG_TRUSTED_CERTS` | No | | As `LIBRENMS_TRUSTED_CERTS`, for Graylog |
| `MCP_TRANSPORT` | No | `stdio` | `stdio`, where the client starts the server, or `http` to [share one server](#share-it-over-http) |
| `MCP_HTTP_TOKENS` | With `http` | | The bearer tokens that may use the server, as `label:token` separated by commas. Labels are up to 50 letters, digits, dots, hyphens or underscores, and say whose token it is in the audit trail. Tokens need at least 32 characters. The server doesn't start over HTTP without one. |
| `MCP_HTTP_PORT` | No | `8080` | The port to listen on |
| `MCP_HTTP_ADDRESS` | No | `127.0.0.1` | The IP address to listen on, or `*` for every address (the default inside the container image) |
| `MCP_HTTP_ALLOWED_ORIGINS` | No | | Values of the `Origin` header to accept, such as `https://tools.example.net`, for a client that sends one. Every other request with an `Origin` is refused. It doesn't let web pages call the server: there's no CORS support. |
| `MCP_MAX_CONCURRENT_REQUESTS` | No | `8` | The most requests in flight to each of LibreNMS, Unimus and Graylog (1 to 256). More wait their turn, which never counts towards `LIBRENMS_TIMEOUT`. A call that waited a second or more says so in its result's `throttling`, so the assistant can slow down. |
| `MCP_RATE_LIMIT_PER_MINUTE` | No | `0` | The most tool calls each client may make in any minute (up to 10000), or `0` for no limit. Over HTTP each token is a client. A call over the limit isn't run, and its result says how long to wait. |
| `MCP_RESOURCES_DIR` | No | | A folder of [reference files](#your-own-reference-files) to offer as resources. A full path. A folder that isn't there stops the server at startup. |
| `MCP_LAST_KNOWN_GOOD` | No | `true` | Whether to give [the last known answer](#when-librenms-cant-be-reached), marked as stale, when LibreNMS can't be reached |
| `MCP_LAST_KNOWN_GOOD_HOURS` | No | `24` | How old such an answer may be (1 to 168 hours) |
| `MCP_LAST_KNOWN_GOOD_DIR` | No | | A folder to keep them in as well as in memory, encrypted, so they outlast a restart. A full path; in the container, mount a volume at `/data` and set it to `/data`. |
| `MCP_TOOLS_ALLOW` | No | | Offer only these tools, by name and comma-separated, such as `find_device,device_health`. Unset, every tool is on. |
| `MCP_TOOLS_DENY` | No | | Turn these tools off, by name and comma-separated, such as `config_backup,config_diff,recent_config_changes,what_changed` for every one that can return something from Unimus. It wins over `MCP_TOOLS_ALLOW`. A tool that's off isn't listed and can't be called. A name that isn't a tool stops the server at startup, and the startup log says which tools are on. |
| `MCP_HELP_REFRESH` | No | `false` | Whether `dashynms_help` may fetch the user guides for each product's release from `raw.githubusercontent.com`. Off, it uses the copies that come with the server. Nothing about your network or a question is in the request; GitHub sees this server's IP address, as any website would. |
| `AUDIT_GELF_URL` | No | | A Graylog GELF input that gets [the audit trail](#the-audit-trail) as well: `https://` or `http://` for a GELF HTTP input (`/gelf` unless you give a path), or `udp://` for a GELF UDP one (port 12201 unless you give one) |
| `AUDIT_GELF_TRUSTED_CERTS` | No | | As `LIBRENMS_TRUSTED_CERTS`, for `AUDIT_GELF_URL` |
| `MCP_MAX_RESULT_BYTES` | No | `16384` | The most JSON one tool call returns. It's sent twice, as text and as structured content, so the message is up to about twice as long. |

### Your own reference files

Point `MCP_RESOURCES_DIR` at a folder of runbooks, naming conventions, site lists or notes on what normal looks like, and each file appears in your client as a resource it can attach. In the container, mount it read-only:

```bash
docker run -i --rm --env-file dashynms.env -e MCP_RESOURCES_DIR=/resources -v /srv/dashynms/resources:/resources:ro ghcr.io/dashynms/mcp:latest
```

- Markdown, text, CSV, JSON and YAML files in UTF-8, up to 128 KB each and 200 in all, in folders up to eight deep. Hidden files and links aren't served, so a Kubernetes ConfigMap needs its files mounted with `subPath`.
- They're read as they are when asked for, never written to, and redacted like a tool's results. Over HTTP, everyone with a token can read them.
- Each is marked as data, not instructions, where it's listed and where it's read.

### When LibreNMS can't be reached

A question asked before is answered with its last good answer, for up to a day. The result has `stale: true`, `as_of` with when it was true and `stale_reason`, its summary says so too, and the assistant is told to pass that on. A question that hasn't been asked before, with the same arguments, gets a plain error.

- Only finished answers are kept: nothing partial, and nothing that anything was redacted from. Config, log and graph answers (`config_backup`, `config_diff`, `recent_config_changes`, `what_changed`, `search_logs`, `graph`) are never kept.
- A refused token forgets every answer. A question LibreNMS now refuses, or says there's no such device for, forgets its own.
- Answers are kept in memory, the 500 used last at most. With `MCP_LAST_KNOWN_GOOD_DIR` they're also on disk, encrypted with a key made from the LibreNMS token and this server's settings: another account or configuration can't read them, and neither can someone with only the folder. They belong to one build of the server, so an update starts afresh. With answers there, the server starts even while LibreNMS can't be reached, though never with a refused token or an untrusted certificate. `/ready` still answers `503` meanwhile, as it's about LibreNMS.

### The audit trail

Every tool call leaves one line of JSON on stderr, beside the server's other logs, however the call ends:

```json
{"event":"tool_call","time":"2026-11-24T09:30:00.123Z","transport":"stdio","client_name":"claude-ai","client_version":"1.0","tool":"find_device","arguments":{"query":"core"},"status":"ok","result_bytes":412,"duration_ms":38}
```

| Field | What it holds |
| --- | --- |
| `time` | When the call arrived, in UTC |
| `transport` | `stdio` or `http` |
| `token_label` | Over HTTP, the label of the token used, never the token |
| `client_name`, `client_version` | What the MCP client calls itself. It can say anything, so it isn't proof of who called. |
| `tool` | The tool asked for |
| `arguments` | The arguments that tool takes, with anything that looks like a secret and the server's own credentials redacted. Text over 256 characters is recorded as its length. |
| `undeclared_arguments` | How many other arguments were sent. Their names and values aren't recorded. |
| `status` | `ok`, `stale` (the last known answer, as LibreNMS couldn't be reached), `error` (the tool couldn't answer), `rejected` (no such tool, one that's off, arguments that don't fit, or a call that couldn't be read), `rate_limited`, `cancelled` or `failed` |
| `result_bytes` | The size of the result: its text in UTF-8, plus any image |
| `duration_ms` | How long the call took |

With `AUDIT_GELF_URL` set, entries also go to Graylog, as GELF messages with the same fields (`_tool`, `_token_label`, `_arguments` as JSON text, and so on). Delivery is best effort and never holds up a call: if Graylog can't keep up or can't be reached, the server logs that once and carries on, and stderr still has every entry.

If nothing reads stderr, entries queue and then, past 1000, are lost rather than hold up a call. The next line that gets through is preceded by `{"event":"audit_lost","count":…}`, saying how many.

A request refused before it's a call at all - no valid token, not a valid MCP message, or a protocol version the server doesn't support - isn't a tool call and leaves no entry.

What a tool returned is never recorded. Arguments still name your devices and say what was searched for, so treat the trail as you would any other log of your network.

Sensor thresholds can also be set by this host, including whether they override
LibreNMS sensor limits. See [Host sensor thresholds](docs/DEVELOPMENT.md#host-sensor-thresholds)
for the `MCP_DBM_*`, `MCP_SIGNAL_*`, `MCP_TEMPERATURE_*`, `MCP_FAN_SPEED_*` and
`MCP_OVERRIDE_SENSOR_LIMITS` settings. Unset, assessment uses the existing defaults.

Unimus and Graylog aren't tested at startup. If one can't be reached, the tools that use it say so, and `what_changed` still returns everything else.

**Self-signed or internal CA certificates:** the server can't ask whether to trust a certificate, so if yours doesn't pass the normal checks, startup stops. It prints the certificate's subject, issuer, dates and fingerprint. Check the fingerprint against your server's own certificate, then add it to `LIBRENMS_TRUSTED_CERTS`. Only that exact certificate is trusted, so when it's renewed you add the new one. There's no setting that trusts every certificate.

## Share it over HTTP

One server can answer a whole team over MCP's Streamable HTTP. Give each person or client a token of their own, so the [audit trail](#the-audit-trail) says who asked and you can take one away without changing the rest:

```bash
openssl rand -hex 32   # once per person
docker run -d --name dashynms-mcp --restart unless-stopped -p 127.0.0.1:8080:8080 --env-file dashynms.env ghcr.io/dashynms/mcp:latest
```

with this in `dashynms.env`, which only its owner should be able to read:

```
LIBRENMS_URL=https://librenms.example.com
LIBRENMS_TOKEN=your-token
MCP_TRANSPORT=http
MCP_HTTP_TOKENS=alice:<alice's token>,bob:<bob's token>
```

The server speaks plain HTTP, so put a reverse proxy with TLS in front of it and don't publish the port itself. With Caddy that's the whole configuration:

```
mcp.example.net {
    reverse_proxy 127.0.0.1:8080
}
```

With nginx, turn buffering off, as answers are streamed:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_buffering off;
    proxy_read_timeout 300s;
}
```

Then point each client at `/mcp` with its token. In Claude Code:

```bash
claude mcp add --transport http dashynms https://mcp.example.net/mcp --header "Authorization: Bearer <alice's token>"
```

- Everything but the two probes needs a token. Without a valid one the answer is `401` and no tool runs.
- `/health` answers `ok` while the server is running, and `/ready` answers `ready`, or `503` when LibreNMS doesn't answer it. Neither needs a token or says anything else. LibreNMS is checked at most once every 15 seconds however often `/ready` is asked, and that check isn't counted in `MCP_MAX_CONCURRENT_REQUESTS`.
- Everyone gets the same tools and sees what the server's own LibreNMS, Unimus and Graylog accounts can see. A token is permission to use the server, not a LibreNMS login.
- `MCP_RATE_LIMIT_PER_MINUTE` counts each token's calls separately, whichever sessions they come in.
- A token can have 64 requests open at once, streams included; past that the answer is `429`.
- The image has no shell or `curl` for a Docker `HEALTHCHECK`, so point your proxy's or orchestrator's HTTP check at the probes.
- To change tokens, change the setting and restart. There's no OAuth yet.

## Help and feedback

- [User guide](docs/guide/README.md): installing, connecting an assistant, sharing it with a team and troubleshooting.
- [Help and support](https://dashynms.pckp.net/mcp/support/): setting it up, and answers to common questions.
- Found a bug or have an idea? [Open an issue](https://github.com/DashyNMS/mcp/issues). Planned work is tracked there too, under the [milestones](https://github.com/DashyNMS/mcp/milestones).

---

<sub>DashyNMS isn't affiliated with LibreNMS, Graylog, Unimus or Anthropic. LibreNMS, Graylog, Unimus and Claude are trademarks of their respective owners.<br>
Project-owned code and assets are MIT licensed; see [LICENSE](LICENSE) and [notices](NOTICES.md). The container image carries both, plus its dependencies' licences in `/app/third-party`.<br>
Found a security problem? See [SECURITY.md](SECURITY.md). Technical notes are in the [developer guide](docs/DEVELOPMENT.md).</sub>
