# Troubleshooting

When the server can't start, it says why in one line. Claude Desktop shows it under **Settings → Developer**. Otherwise, start the server yourself in a terminal with the same settings and read what it prints.

## The assistant can't see the tools

1. Quit the assistant completely and open it again. Closing its window may leave it running.
2. Check that the configuration is valid JSON and that the path or image name is right.
3. With Docker, check that Docker is running and that `-i` is in the arguments.
4. Look for the server's line of explanation, as above.
5. Check whether the tool has been [turned off](../how-to/control-access.md#turn-tools-off).

## It can't reach LibreNMS

1. Open the address in a browser on the computer that runs the server.
2. From a container, `localhost` is the container itself. Use an address the container can reach.
3. Check any VPN, proxy or firewall in between.

## The token is refused

Check that it's copied in full, hasn't been revoked, and that its user may use the API.

## The certificate isn't trusted

The server prints the certificate's fingerprint and stops.

1. Compare the fingerprint with your server's own certificate.
2. Add it to `LIBRENMS_TRUSTED_CERTS`.

Only that certificate is trusted, so add the new one when it's renewed. `UNIMUS_TRUSTED_CERTS` and `GRAYLOG_TRUSTED_CERTS` work the same way.

## Something is missing from an answer

1. Look at it in LibreNMS with the same user. The server can't see what that user can't.
2. Ask about a shorter time or one device.
3. For sensors, see [See every sensor](permissions.md#see-every-sensor).

## Times are wrong by whole hours

Set `LIBRENMS_TIMESTAMPS_UTC` to `false` if LibreNMS stores local times. This works with the program, not with Docker, which has no time zone data.

## An answer says it's slowed down or over the limit

The server is protecting LibreNMS. Ask less at once, or change the [limits](../how-to/control-access.md#limit-requests).

## Over HTTP

- `401` means the token is missing or wrong.
- `403` means the request's `Origin` isn't allowed, or a session belongs to another token.
- `429` means that token has too many requests open.
- `503` from `/ready` means LibreNMS doesn't answer.

## Report a problem

[Open an issue](https://github.com/DashyNMS/mcp/issues) with the version, how you run it, what you asked and what came back. Remove tokens, names and addresses first.
