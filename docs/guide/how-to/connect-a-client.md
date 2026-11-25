# Connect your assistant

Your assistant starts the server and talks to it directly, so you give it three things: how to start the server, your LibreNMS address and your token.

The examples use `https://librenms.example.net`. Replace it and `your-token` with your own.

The Docker examples use `latest`, the newest release. Until 2.0 is released, use `nightly` in its place for anything in this guide that 1.1 doesn't have.

## Claude Code

With Docker, put your settings in a file that only you can read, one per line and without quotes. That keeps the token out of your shell's history:

```
LIBRENMS_URL=https://librenms.example.net
LIBRENMS_TOKEN=your-token
```

Then add the server with the file's full path:

```bash
claude mcp add dashynms -- docker run -i --rm --env-file /home/you/dashynms.env ghcr.io/dashynms/mcp:latest
```

With the program, set `LIBRENMS_TOKEN` where your shell keeps its secrets, and let Claude Code read it from there. In `.mcp.json` in your project:

```json
{
  "mcpServers": {
    "dashynms": {
      "command": "/opt/dashynms/dashynms-mcp",
      "env": { "LIBRENMS_URL": "https://librenms.example.net", "LIBRENMS_TOKEN": "${LIBRENMS_TOKEN}" }
    }
  }
}
```

## Claude Desktop with Docker

1. Put your settings in a file that only you can read, as above.
2. In Claude Desktop, open **Settings → Developer → Edit Config** and add:

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

3. Quit Claude Desktop completely, from its tray or menu bar icon, then open it again.

## Claude Desktop with the program

Follow the same steps with this instead. `command` is the program's full path:

```json
{
  "mcpServers": {
    "dashynms": {
      "command": "C:\\path\\to\\dashynms-mcp.exe",
      "env": { "LIBRENMS_URL": "https://librenms.example.net", "LIBRENMS_TOKEN": "your-token" }
    }
  }
}
```

## Another assistant

Any client that can start an MCP server over stdio works the same way: the command is `docker` with the arguments above, or the program's path with no arguments. Settings are environment variables. A client that connects to a web address needs [a server shared over HTTP](share-over-http.md).

## Check that it works

Ask "Which devices are down?". When the server starts it tests your address and token first. If something's wrong it stops and says what in one line. Claude Desktop shows that line under **Settings → Developer**; in Claude Code, run `claude mcp list`.

If your server has a self-signed certificate, see [Troubleshooting](../reference/troubleshooting.md#the-certificate-isnt-trusted).
