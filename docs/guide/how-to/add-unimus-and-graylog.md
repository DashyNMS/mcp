# Add Unimus and Graylog

Both are optional. Use a read-only account for each.

## Unimus

Set `UNIMUS_URL` and `UNIMUS_TOKEN` together. The assistant can then read and compare configuration backups and find devices whose configuration changed. Unimus 1.7 or later is needed.

If LibreNMS knows a device as `core-sw1` and Unimus as `core-sw1.example.net`, set `UNIMUS_MY_DOMAIN` to `example.net` so they match.

Choose how much configuration is returned with `MCP_CONFIG_TEXT`:

| Value | What's returned |
| --- | --- |
| `full` | Configuration text with secrets hidden. The default. |
| `summary` | When it changed and how many lines, without any text |
| `off` | Nothing from Unimus |

## Graylog

Set `GRAYLOG_URL`, `GRAYLOG_USERNAME` and `GRAYLOG_PASSWORD`. To use an access token, put the token in the user name and `token` as the password. Graylog 2.1 and later work as they are.

If a device logs from an address other than the one LibreNMS polls, set `GRAYLOG_MATCH_ANY_ADDRESS` to `true`.

Neither is tested when the server starts. If one can't be reached, the tools that need it say so and the rest carry on.
