# Control what assistants can do

Every tool only reads. These settings narrow what's offered, protect your servers and record what's asked.

## Turn tools off

- `MCP_TOOLS_DENY` turns tools off, for example `config_backup,config_diff,recent_config_changes,what_changed` for everything that can return something from Unimus.
- `MCP_TOOLS_ALLOW` offers only the tools you list.

- `MCP_TOOLS_ALLOW=dashynms_help` offers help with DashyNMS alone, and needs no LibreNMS.

Deny wins over allow. A tool that's off isn't listed and can't be called. A name that isn't a tool stops the server when it starts, so a typing mistake can't leave one on. [Tools](../reference/tools.md) has the names.

## Limit requests

- `MCP_MAX_CONCURRENT_REQUESTS` is the most requests in progress to each of LibreNMS, Unimus and Graylog, 8 unless you change it. The rest wait their turn.
- `MCP_RATE_LIMIT_PER_MINUTE` is the most calls each person or client may make in a minute. It's off unless you set it. A call over the limit is refused and told how long to wait.
- `MCP_MAX_RESULT_BYTES` is the most one answer can hold. Longer ones say what was left out.

## Keep an audit trail

Every call leaves one line on the server's error output: when, which client, whose token, which tool, the arguments and how it went. Secrets are hidden and answers are never recorded. The arguments still name your devices, so treat it as you would any log of your network.

To send it to Graylog as well, set `AUDIT_GELF_URL` to a GELF input, such as `https://graylog.example.net:12201` or `udp://graylog.example.net`.

## Decide what happens when LibreNMS can't be reached

A question asked before is answered with its last good answer, marked as stale with its time. It's kept for 24 hours, in memory. Answers about configuration, logs and graphs are never kept, and neither is anything incomplete.

- Set `MCP_LAST_KNOWN_GOOD` to `false` to turn this off.
- Set `MCP_LAST_KNOWN_GOOD_HOURS` to change how old an answer may be.
- Set `MCP_LAST_KNOWN_GOOD_DIR` to a folder to keep them after a restart, encrypted.

A refused token forgets every kept answer.
