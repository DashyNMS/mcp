# Share one server over HTTP

One server can answer a whole team. Everyone sees what the server's own LibreNMS, Unimus and Graylog accounts can see: a token is permission to use the server, not a LibreNMS sign-in.

## Start it

1. Make a token of its own for each person:

   ```bash
   openssl rand -hex 32
   ```

2. Put the settings in a file only its owner can read:

   ```
   LIBRENMS_URL=https://librenms.example.net
   LIBRENMS_TOKEN=your-token
   MCP_TRANSPORT=http
   MCP_HTTP_TOKENS=alice:<alice's token>,bob:<bob's token>
   ```

3. Start it:

   ```bash
   docker run -d --name dashynms-mcp --restart unless-stopped -p 127.0.0.1:8080:8080 --env-file dashynms.env ghcr.io/dashynms/mcp:latest
   ```

This needs 2.0. Until it's released, use `nightly` in place of `latest`.

## Put HTTPS in front of it

The server speaks plain HTTP, so don't publish its port. Put a reverse proxy with HTTPS in front. With Caddy, this is the whole configuration:

```
mcp.example.net {
    reverse_proxy 127.0.0.1:8080
}
```

With nginx, turn buffering off:

```nginx
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_buffering off;
    proxy_read_timeout 300s;
}
```

## Connect each person

Point each assistant at `/mcp` with that person's token. In Claude Code, set `DASHYNMS_TOKEN` where your shell keeps its secrets, and add this to `.mcp.json` in your project. The token stays out of the file and your shell's history:

```json
{
  "mcpServers": {
    "dashynms": {
      "type": "http",
      "url": "https://mcp.example.net/mcp",
      "headers": { "Authorization": "Bearer ${DASHYNMS_TOKEN}" }
    }
  }
}
```

## Check on it

- `/health` answers `ok` while the server is running.
- `/ready` answers `ready`, or `503` when LibreNMS doesn't answer. A server that offers only help is always ready.

Neither needs a token. Everything else does, and answers `401` without one.

To add or remove someone, change `MCP_HTTP_TOKENS` and restart the server. The [audit trail](control-access.md#keep-an-audit-trail) records whose token was used.
