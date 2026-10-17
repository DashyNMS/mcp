# Security policy

Thanks for helping keep DashyNMS MCP and the people who use it safe.

## Reporting a vulnerability

Please **don't open a public issue** for a security problem.

Report it privately instead: on this repository, go to **Security → Report a vulnerability**. Only the maintainer can see the report.

It helps to include:

- what the problem is and what someone could do with it
- the steps to reproduce it, the server version, and the MCP client you used
- anything you've found about how to fix it

## What happens next

- You'll get a reply within **5 working days** confirming the report has been received.
- We'll keep you updated while it's investigated and let you know when a fix is released.
- Once it's fixed, you're welcome to be credited in the release notes, or to stay anonymous.

Please give us a reasonable chance to release a fix before you share details publicly.

## Supported versions

Security fixes go into `main` first, so the `edge` image has them as soon as they merge, and then into the **next release** (the `latest` image tag). Please check the problem is still there on `edge` or the latest release before reporting.

You're welcome to test the server you run under its licence for security problems, against your own LibreNMS, Unimus and Graylog. Please don't test against systems you don't own.

## Scope

**In scope:** the DashyNMS MCP server and its published container image (`ghcr.io/dashynms/mcp`), including how it handles your LibreNMS, Unimus and Graylog credentials, what it returns to the model, and how it redacts secrets.

**Out of scope:**

- problems in LibreNMS itself. Please report those to the [LibreNMS project](https://github.com/librenms/librenms/security).
- problems in the MCP client or model you connect to the server
- how a particular LibreNMS server is set up or secured

Known, open security issues are tracked publicly once it's safe to do so.
