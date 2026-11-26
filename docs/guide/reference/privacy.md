# Privacy

## What goes to a model

What a tool returns goes to whichever model your assistant uses: device names, addresses, alerts, ports, log lines and, with Unimus, configuration. Check the terms of that provider before connecting one.

Nothing is sent to DashyNMS. The server contacts only your own LibreNMS, Unimus and Graylog, the audit input if you set one, and GitHub if you turn on guide refreshes.

## User guides

`dashynms_help` searches copies of the guides that come with the server. The search runs on the server, and your question isn't sent anywhere to do it. Like any call, it's in the [audit trail](../how-to/control-access.md#keep-an-audit-trail).

If you set `MCP_HELP_REFRESH` to `true`, the server also contacts `raw.githubusercontent.com` to fetch the guide for each product's release. It asks for public pages by name, and nothing about you, your network or your question is in the request. GitHub sees the server's IP address, as any website would. It's off unless you turn it on.

## What's hidden

Passwords, SNMP communities, keys and tokens are hidden before anything leaves the server, including inside configuration. Redaction can't recognise everything, so if configuration shouldn't leave your network, set `MCP_CONFIG_TEXT` to `summary` or `off`.

Text from your network, such as a device's name or a log line, is marked as information. The assistant is told not to take instructions from it.

## What's kept

- Your token and passwords are read from the environment, never logged, and sent only to the server they're for.
- Recent answers are kept in memory so they can be given if LibreNMS goes down. On disk they're kept only if you choose a folder, and they're encrypted. See [old answers](../how-to/control-access.md#decide-what-happens-when-librenms-cant-be-reached).
- The [audit trail](../how-to/control-access.md#keep-an-audit-trail) records questions, not answers. Where it goes depends on how you run the server.
- Your assistant keeps its own history.

Use `https://` addresses wherever you can. The server warns when a token would travel unencrypted.
