# Developer guide

## Layout

| Path | What it is |
| --- | --- |
| `src/DashyNMS.Agent` | The tool layer: tools, redaction, result caps, device lookup. A plain class library, so it can later move to the desktop repo for the mobile app's chat tab. |
| `src/DashyNMS.Mcp` | The host: settings, the startup check, and MCP over stdio |
| `tests/DashyNMS.Agent.Tests` | Tools against recorded LibreNMS JSON through Core's real APIs, plus redaction and caps |
| `tests/DashyNMS.Mcp.Tests` | Settings, the startup check, and an end-to-end stdio test against a stand-in LibreNMS |

## Core

The LibreNMS client, models and alerting logic come from [DashyNMS/core](https://github.com/DashyNMS/core) as the `DashyNMS.Core` package on GitHub Packages - the same Core the desktop and phone apps use. `Directory.Build.props` pins it (`DashyNMSCoreVersion`) to a development build of core's main (`2.0.0-ci.N`) between Core releases. Fixes to Core go to the core repository.

GitHub Packages needs a login even for a public package: set `DASHYNMS_PACKAGES_TOKEN` to a token with `read:packages`. Locally, `gh auth token` works once `gh auth refresh -s read:packages` has added the scope; in Actions, `secrets.GITHUB_TOKEN` with `packages: read` (the package grants this repository read access).

To build against a local checkout of DashyNMS/core instead - a Core change and a server change together - set `DASHYNMS_LOCAL_CORE` to that checkout's folder.

## Build and test

You need the .NET 10 SDK and `DASHYNMS_PACKAGES_TOKEN` (see Core above).

```bash
git clone https://github.com/DashyNMS/mcp.git
cd mcp
dotnet build DashyNMS.Mcp.slnx
dotnet test DashyNMS.Mcp.slnx
```

Warnings are errors in this repo's own projects.

To run the server outside Docker, publish it once and point your client at the published program:

```bash
dotnet publish src/DashyNMS.Mcp -c Release -o publish
```

Don't use `dotnet run` in a client's configuration. It can write build output to stdout, which carries the MCP protocol.

## Container image

```bash
docker build -t dashynms-mcp .
```

The Dockerfile cross-compiles for the target architecture, so `docker buildx build --platform linux/arm64 .` is as fast as a native build. Base images are pinned by digest and kept current by Dependabot.

`.github/workflows/image.yml` builds every pull request and smoke-tests it: with no settings, the image must exit with code 2 and name what's missing. It publishes to `ghcr.io/dashynms/mcp`:

- every merge to `main` updates `edge` and adds `sha-<commit>`,
- a `vX.Y.Z` tag publishes `X.Y.Z`, `X.Y` and `latest`,
- a pre-release tag such as `v1.2.0-rc.1` gets its own tag only, and doesn't move `latest`.

To release, set `<Version>` in `Directory.Build.props` to the new version and merge that first: it's the version the server reports to clients. Then tag `main` with `vX.Y.Z`.

## Rules for tools

- Every tool is read-only, and its MCP annotations say so. A test fails if one isn't.
- Every string a tool returns goes through the redactor. Add a failing test to `RedactorTests` before fixing a gap.
- Summaries and error messages are our own sentences. Device text goes only under `data` or `details`.
- Tool and parameter descriptions are prompts: review them like code.
- Logs go to stderr only, and never include a token or password.

## Commits, pull requests and issues

Every change goes through a branch and a pull request into `main`, one issue per PR, and is squash-merged, so the PR title becomes the commit on `main`. PR titles, commit messages and issue titles follow [Conventional Commits](https://www.conventionalcommits.org/) in plain words, the same standard as every DashyNMS repository:

```
type(scope): what changes, as the user would notice it
```

- **type:** `feat` (new), `fix` (was wrong, now works), `perf`, `refactor` (no behaviour change), `docs`, `test`, `build` (projects, packages, versions), `ci`, `chore`, `revert`.
- **scope:** the area, in lower case: `tools`, `alerts`, `devices`, `events`, `health`, `ports`, `graphs`, `logs`, `graylog`, `unimus`, `config`, `redaction`, `settings`, `transport`, `image`, `core`, `deps`. Separate two with a comma, or leave it out when there's no single area. A new area can add a scope; add it here in the same PR.
- **description:** says what changes, not how. Lower-case start, no full stop, the whole title under about 72 characters.
- **Issue numbers:** never in the title. `Closes #NN` goes in the PR description, which then says briefly what changed and how it was checked.
- **Breaking changes:** a `!` after the scope, and a sentence saying what breaks.
- **Issue titles:** the same form, worded as the result the fixing PR will deliver, so the PR can reuse the title. An epic ends with `(epic)`. Labels carry the rest.
- **Attribution:** commits, PRs and release notes carry no AI attribution or `Co-Authored-By` lines.

Examples:

```
fix(alerts): recoveries match by fault_id
feat(tools): list device groups and locations
build(image): smaller container image
docs: setting up Claude Desktop
```
