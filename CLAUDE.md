# DashyNMS MCP

Read-only MCP server for LibreNMS that uses DashyNMS Core.

- Core is the pinned `DashyNMS.Core` package from [DashyNMS/core](https://github.com/DashyNMS/core). Restoring needs `DASHYNMS_PACKAGES_TOKEN` with `read:packages`; set `DASHYNMS_LOCAL_CORE` to build against a local checkout. Core fixes go to the Core repository.
- `src/DashyNMS.Agent` is the MCP tool layer and `src/DashyNMS.Mcp` is the console host. Both stay here, so the server runs standalone for any MCP client.
- `src/DashyNMS.Agent.Shared` supplies typed operations over a host-owned Core client, without a `ModelContextProtocol` reference. Mobile pins this package and never references `DashyNMS.Agent`. A change for another host leaves MCP's output unchanged unless every MCP client wants it.
- Every tool is read-only. Adding a write tool requires an approval step. Run every returned string through the redactor.
- stdout carries the stdio MCP protocol; log to stderr only. Configuration comes only from environment variables. Never log or echo a token or password.
- Use sealed classes and XML doc comments explaining why.

## Repository conventions

- Before changing build, test or publication workflows, read [the developer guide](docs/DEVELOPMENT.md). Before changing release notes, read its [Release notes](docs/DEVELOPMENT.md#release-notes) section.
- Before changing shared conventions or security checks, read [Repository standards](docs/REPOSITORY-STANDARDS.md); update the canonical source and every affected consumer.
- Create or find an issue describing the bug or intended change before opening any PR, including dependency, documentation and CI changes. Every PR must reference its issue.
- When filing an issue, set its organisation type, relevant labels and milestone. Follow [issue categorisation](https://github.com/DashyNMS/.github/blob/main/docs/REPOSITORY-STANDARDS.md#issue-categorisation) for milestone choices and API verification.
- Work on a branch; use one issue per PR into `main`, then squash-merge. The PR title becomes the commit subject on `main`.
- PR titles, branch commits and issue titles use `type(scope): what changes, as the user would notice it`. Read [Commits, pull requests and issues](docs/DEVELOPMENT.md#commits-pull-requests-and-issues) for this repository's types, scopes and examples.
- Start the description in lower case, omit the final full stop and aim for a complete subject under 72 characters. Separate two scopes with a comma; omit the scope when there is no single area. Document a new scope in the developer guide in the same PR.
- Word issue titles as the result the fixing PR delivers. Epics end with `(epic)`. Keep issue numbers out of titles; start the PR body with `Closes #NN` when resolving an issue, then explain what changes and how it was checked.
- Mark breaking changes with `!` and explain the migration and version implications for affected consumers.
- Dependabot uses `build(deps): ...` for every ecosystem. Authenticated Dependabot may use generated capitalisation and exceed 72 characters.
- Use British spelling in user-facing text. Keep real device names, hostnames, addresses, credentials and other identifiable network data out of issues, PRs, commits, logs, screenshots and fixtures. Use `example.net` and documentation address ranges.
- Commits, PRs and release notes carry no AI attribution or `Co-Authored-By` lines.
- Read `LICENSE` before changing or redistributing repository material. Preserve third-party licences, copyright notices and provenance.

## Validation and releases

- Run `dotnet build DashyNMS.Mcp.slnx` and `dotnet test DashyNMS.Mcp.slnx`. Warnings are errors in this repository's own projects.
- Changes to server behaviour also update `docs/guide` in the same PR. Before editing the guide or wiki publishing, read [User guide and wiki](docs/DEVELOPMENT.md#user-guide-and-wiki).
- A change users would notice adds or updates its line in `docs/releases/<version>.md`, using the version in `Directory.Build.props`, in the same PR. GitHub release notes come from that file.
- `PRSMNCA` means open a PR, then squash-merge it with no co-author.
