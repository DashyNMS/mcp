# DashyNMS MCP user guide

<img src="https://dashynms.pckp.net/assets/img/favicon.svg" alt="DashyNMS" width="80" height="80">

**Ask an AI assistant about your LibreNMS network.**

DashyNMS MCP lets Claude, or any other assistant that speaks the Model Context Protocol, answer questions from your own LibreNMS server. It only reads. This guide is for anyone who runs the server themselves, or uses it through DashyNMS on a computer.

[Website](https://dashynms.pckp.net/mcp/) · [Downloads](https://github.com/DashyNMS/mcp/releases) · [Report a problem](https://github.com/DashyNMS/mcp/issues)

These pages cover DashyNMS MCP 2.0. Until 2.0 is released, use a nightly build to follow along.

## Start here

- [Getting started](getting-started.md): what the server does, what you need, and your first question.
- [Use it inside DashyNMS Desktop](how-to/use-with-desktop.md): nothing to install, if you use the app.
- [Run it with Docker](installation/install-docker.md)
- [Run it without Docker](installation/install-program.md)

## Everyday tasks

- [Connect your assistant](how-to/connect-a-client.md): Claude Code, Claude Desktop and other clients.
- [Ask good questions](how-to/ask-questions.md): what you can ask, and what to do when an answer is incomplete.
- [Use prompts and your own reference files](how-to/prompts-and-files.md): ready-made investigations, runbooks and site notes.

## Run it for your team

- [Add Unimus and Graylog](how-to/add-unimus-and-graylog.md): configuration backups and log messages.
- [Share one server over HTTP](how-to/share-over-http.md): one server, a token for each person.
- [Control what assistants can do](how-to/control-access.md): turn tools off, limit requests and keep an audit trail.

## Help and reference

- [Accounts and permissions](reference/permissions.md): the token to make, and what it needs to see.
- [Tools](reference/tools.md): every tool and what it answers.
- [Settings](reference/settings.md): every setting and its default.
- [Privacy](reference/privacy.md): what's sent to a model, what's hidden and what's kept.
- [Troubleshooting](reference/troubleshooting.md): start-up, connection and answers that look wrong.

For DashyNMS itself, see the [Desktop user guide](https://github.com/DashyNMS/desktop/wiki) and the [Mobile user guide](https://github.com/DashyNMS/mobile/wiki). For a security problem, use the [security reporting instructions](../../SECURITY.md).
