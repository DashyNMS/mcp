# Settings

Every setting is an environment variable. Only the LibreNMS address and token are required, and not even those when `MCP_TOOLS_ALLOW` is just `dashynms_help`.

## LibreNMS

| Setting | Default | What it's for |
| --- | --- | --- |
| `LIBRENMS_URL` | required | Your LibreNMS address |
| `LIBRENMS_TOKEN` | required | A read-only user's API token |
| `LIBRENMS_BACKUP_URL` | | Another address for the same server |
| `LIBRENMS_TRUSTED_CERTS` | | Certificate fingerprints to trust, comma-separated |
| `LIBRENMS_TIMEOUT` | `30` | Seconds to wait for a request, at least 5 |
| `LIBRENMS_TIMESTAMPS_UTC` | `true` | Whether LibreNMS stores times in UTC. Leave it on with Docker. |

## Unimus and Graylog connections

| Setting | Default | What it's for |
| --- | --- | --- |
| `UNIMUS_URL`, `UNIMUS_TOKEN` | | Turn Unimus on |
| `UNIMUS_MY_DOMAIN` | | The domain LibreNMS adds to short names, such as `example.net` |
| `UNIMUS_TRUSTED_CERTS` | | Certificate fingerprints to trust |
| `MCP_CONFIG_TEXT` | `full` | `full`, `summary` or `off` |
| `GRAYLOG_URL`, `GRAYLOG_USERNAME`, `GRAYLOG_PASSWORD` | | Turn Graylog on |
| `GRAYLOG_VERSION` | `2.1` | `2.0` only for Graylog 2.0 |
| `GRAYLOG_QUERY_FIELD` | `source` | The field matched against a device's addresses |
| `GRAYLOG_MATCH_ANY_ADDRESS` | `false` | Match every address on a device's interfaces |
| `GRAYLOG_TRUSTED_CERTS` | | Certificate fingerprints to trust |

## Sharing over HTTP

| Setting | Default | What it's for |
| --- | --- | --- |
| `MCP_TRANSPORT` | `stdio` | `stdio`, where the assistant starts the server, or `http` |
| `MCP_HTTP_TOKENS` | | Who may use it, as `label:token`, comma-separated. Tokens need at least 32 characters. |
| `MCP_HTTP_PORT` | `8080` | The port to listen on |
| `MCP_HTTP_ADDRESS` | `127.0.0.1` | The address to listen on, or `*` for all. The image uses `*`. |
| `MCP_HTTP_ALLOWED_ORIGINS` | | `Origin` values to accept, for a client that sends one |

## Tools, limits and audit

| Setting | Default | What it's for |
| --- | --- | --- |
| `MCP_TOOLS_ALLOW`, `MCP_TOOLS_DENY` | | [Turn tools off](../how-to/control-access.md#turn-tools-off) |
| `MCP_MAX_CONCURRENT_REQUESTS` | `8` | Requests in progress to each service, 1 to 256 |
| `MCP_RATE_LIMIT_PER_MINUTE` | `0` | Calls each client may make in a minute, or `0` for no limit |
| `MCP_HELP_REFRESH` | `false` | Fetch newer [user guides](privacy.md#user-guides) from GitHub for `dashynms_help` |
| `MCP_MAX_RESULT_BYTES` | `16384` | The most one answer holds |
| `AUDIT_GELF_URL` | | A GELF input for the audit trail |
| `AUDIT_GELF_TRUSTED_CERTS` | | Certificate fingerprints to trust |

## Kept answers and files

| Setting | Default | What it's for |
| --- | --- | --- |
| `MCP_LAST_KNOWN_GOOD` | `true` | Give the last good answer when LibreNMS can't be reached |
| `MCP_LAST_KNOWN_GOOD_HOURS` | `24` | How old it may be, 1 to 168 hours |
| `MCP_LAST_KNOWN_GOOD_DIR` | | A folder to keep answers in after a restart |
| `MCP_RESOURCES_DIR` | | A folder of [your own files](../how-to/prompts-and-files.md#add-your-own-files) |

## Sensor limits

`MCP_DBM_*`, `MCP_SIGNAL_*`, `MCP_TEMPERATURE_*` and `MCP_FAN_SPEED_*` set the limits readings are judged by, and `MCP_OVERRIDE_SENSOR_LIMITS` makes them win over the limits in LibreNMS. The [developer guide](../../DEVELOPMENT.md#host-sensor-thresholds) lists them.
