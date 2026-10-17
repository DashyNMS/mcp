# DashyNMS MCP

Read-only MCP server that lets Claude (and other MCP clients) answer questions about LibreNMS, reusing DashyNMS desktop's Core library.

- `external/desktop` is a git submodule of DashyNMS/desktop (tracking `master`). Run `git submodule update --init` if it's empty. Never edit files under it here - upstream fixes go to the desktop repo.
- `src/DesktopNMS.Core.Portable` compiles the desktop Core sources for net10.0, the same way DashyNMS/mobile does. `src/DashyNMS.Agent` is the tool layer (a plain class library that later moves to the desktop repo so the mobile chat tab can share it). `src/DashyNMS.Mcp` is the host (a console app).
- Build and test: `dotnet build DashyNMS.Mcp.slnx` and `dotnet test tests/DashyNMS.Agent.Tests`. Warnings are errors in this repo's own projects.
- Every tool is read-only. Don't register a write tool without an approval step, and run every string a tool returns through the redactor.
- stdout carries the MCP protocol over stdio: log to stderr only.
- Configuration comes only from environment variables. Never log or echo a token or password.
- Commits, PRs and release notes carry no AI attribution or Co-Authored-By lines. "PRSMNCA" means open a PR, then squash-merge it with no co-author; larger branches whose commits are worth keeping are rebase-merged instead.
- Match desktop's style: British spelling in user-facing text, XML doc comments explaining *why*, sealed classes.
