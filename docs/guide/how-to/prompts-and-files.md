# Use prompts and your own reference files

## Start from a prompt

Three prompts appear in your assistant's prompt menu. Each one takes it through the right tools in order:

- **Investigate this device**
- **Morning check: what's down and why**
- **What changed before this alert?**

A prompt whose main tool is [turned off](control-access.md#turn-tools-off) isn't offered.

## Add your own files

Point the server at a folder of runbooks, naming conventions or site notes, and your assistant can attach them.

1. Put the files in a folder: Markdown, text, CSV, JSON or YAML, up to 128 KB each and 200 in all.
2. Set `MCP_RESOURCES_DIR` to the folder's full path. With Docker, mount it read-only:

   ```bash
   docker run -i --rm --env-file dashynms.env -e MCP_RESOURCES_DIR=/resources -v /srv/dashynms/resources:/resources:ro ghcr.io/dashynms/mcp:latest
   ```

3. Restart the server.

Hidden files and links aren't offered. Files are read when they're asked for and never changed. Secrets in them are hidden as they are in other answers, and each is marked as information rather than instructions. Over HTTP, everyone with a token can read them, so keep anything private out.
