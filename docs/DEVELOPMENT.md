# Developer guide

## Layout

| Path | What it is |
| --- | --- |
| `external/desktop` | [DashyNMS/desktop](https://github.com/DashyNMS/desktop) as a git submodule, tracking `master`. Never edit it here; fixes go to the desktop repo. |
| `src/DesktopNMS.Core.Portable` | Desktop's Core library, compiled from the submodule for net10.0, the same way mobile does it |
| `src/DashyNMS.Agent` | The tool layer: tools, redaction, result caps, device lookup. A plain class library, so it can later move to the desktop repo for the mobile app's chat tab. |
| `src/DashyNMS.Mcp` | The host: settings, the startup check, and MCP over stdio |
| `tests/DashyNMS.Agent.Tests` | Tools against recorded LibreNMS JSON through Core's real APIs, plus redaction and caps |
| `tests/DashyNMS.Mcp.Tests` | Settings, the startup check, and an end-to-end stdio test against a stand-in LibreNMS |

## Build and test

You need the .NET 10 SDK.

```bash
git clone --recurse-submodules https://github.com/DashyNMS/mcp.git
cd mcp
dotnet build DashyNMS.Mcp.slnx
dotnet test DashyNMS.Mcp.slnx
```

If you cloned without `--recurse-submodules`, run `git submodule update --init` first. Warnings are errors in this repo's own projects.

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
