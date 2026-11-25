# Getting started

## What it is

DashyNMS MCP sits between an AI assistant and your LibreNMS server. The assistant asks it for facts, such as open alerts or the state of a port, and it looks them up and hands them back. It has no way to change your network.

You may not need to install anything. DashyNMS on a computer has its own copy; see [Use it inside DashyNMS Desktop](how-to/use-with-desktop.md). Run the server yourself to use Claude Desktop, Claude Code or another assistant, or to share it with a team.

## What you need

- A LibreNMS server you can reach, with its API turned on.
- An API token for a **read-only** LibreNMS user. [Accounts and permissions](reference/permissions.md) explains how to make one.
- Docker, or the [program for your computer](installation/install-program.md).
- An assistant that can use MCP servers.

## Set it up

1. Make the LibreNMS token.
2. Get the server: [with Docker](installation/install-docker.md) or [without](installation/install-program.md).
3. [Connect your assistant](how-to/connect-a-client.md), giving it the address and token.
4. Restart the assistant if it asks you to.

## Ask your first question

Ask "Which devices are down?" or "How's the network?". The assistant may ask your permission to use a DashyNMS tool the first time.

If the assistant says it has no DashyNMS tools, see [Troubleshooting](reference/troubleshooting.md#the-assistant-cant-see-the-tools).
