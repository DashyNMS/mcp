# DashyNMS MCP

Read-only MCP server that lets Claude (and other MCP clients) answer questions about LibreNMS, reusing DashyNMS desktop's Core library.

- Core (the LibreNMS client and models) is the `DashyNMS.Core` package from DashyNMS/core on GitHub Packages, pinned in `Directory.Build.props`. Restoring needs `DASHYNMS_PACKAGES_TOKEN` (for example `gh auth token`); set `DASHYNMS_LOCAL_CORE` to build against a local core checkout. Core fixes go to the core repo.
- `src/DashyNMS.Agent` is the tool layer (a plain class library that later moves to the desktop repo so the mobile chat tab can share it). `src/DashyNMS.Mcp` is the host (a console app).
- Build and test: `dotnet build DashyNMS.Mcp.slnx` and `dotnet test DashyNMS.Mcp.slnx`. Warnings are errors in this repo's own projects.
- Every tool is read-only. Don't register a write tool without an approval step, and run every string a tool returns through the redactor.
- stdout carries the MCP protocol over stdio: log to stderr only.
- Configuration comes only from environment variables. Never log or echo a token or password.
- PR titles, commits and issue titles: `type(scope): what changes, in plain words`. One issue per PR, `Closes #NN` in the description, issue titles worded as the result the fix delivers. Full standard and scopes: docs/DEVELOPMENT.md, "Commits, pull requests and issues".
- Commits, PRs and release notes carry no AI attribution or Co-Authored-By lines. "PRSMNCA" means open a PR, then squash-merge it with no co-author; larger branches whose commits are worth keeping are rebase-merged instead.
- Match desktop's style: British spelling in user-facing text, XML doc comments explaining *why*, sealed classes.
- Never put real device names, hostnames, addresses or other identifiable network data in issues, PRs, commits or test fixtures.
