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

## Typed device search for host-owned clients

`Devices.DeviceSearch.FindDevicesAsync(FindDevicesRequest, CancellationToken)`
returns typed `DeviceMatch` rows, ARP observations and exact `PageInfo`. Both
MCP's `find_device` and other hosts use this execution; JSON is an MCP
presentation format, not the shared API. Ranking, status/scope filtering, MAC
lookup and paging remain in the executor. Results contain redacted summaries;
matching and sorting use original device fields. The MCP adapter retains brief
and verbose records, summaries and its additional JSON byte cap.

A host can call `DeviceSearch.Create(capturedClient, clock, options)` without
calling `AddDashyNmsAgent`. The factory rejects null dependencies, creates fresh
device/scope caches and registers or replaces no host services. Supply a client
bound to one immutable connection generation. Cancel calls and discard the
executor on switch/sign-out/settings change. Do not wrap a mutable session client
and assume cache ownership alone prevents requests reaching a new connection.
An internal constructor accepts existing caches for MCP's shared directory lifetime.

Cancellation reaches resource APIs and the directory semaphore. Already-cancelled
calls fail even with warm caches; cancelled late directory responses are not
cached, and execution checks cancellation before returning results.

Each cache fetch belongs to the caller holding its semaphore. If that owner is
cancelled, a late successful response is discarded; a waiting uncancelled caller
then fetches again with its own token. Cancelling a waiter does not cancel the
owner. An uncooperative owner transport holds the semaphore until it completes.
Hosts still need their own linked deadline/WaitAsync bound for uncooperative
transports.
The executor does not log, but propagated exceptions can contain sensitive data.
Adapters own the exception policy: mobile must map them to fixed safe statuses and must not expose exception messages,
ToolException details, server bodies or exception objects in logs/model output.
MCP continues to apply ToolRunner's existing server error policy.

This is the first extraction for mobile #596, not a completed mobile integration.
The Agent assembly still references MCP; a separate dependency-free assembly,
source-generated/static mobile dispatch, safe typed failure/freshness metadata,
Core version reconciliation, immutable package/fixture publication and runtime
trimming/AOT proofs remain follow-up requirements before shipping on mobile.
Redaction recognises secret patterns; returned network strings remain untrusted
and need the host's marker/control sanitisation and presentation budget.

`DeviceSearchTests` uses synthetic transport responses for MCP/typed parity,
filters and paging, and controlled APIs for ranking/redaction, generation cache
isolation, pre-cancellation and late-response cancellation. Existing search tests
continue to cover the MCP brief/verbose and MAC output contracts. These tests
must run in CI; they were not executed locally for this extraction.

### Extraction boundaries and privacy limits

Paging selects raw matches before summary construction and redaction, including
ARP groups. The typed API caps rows, but has no string-length or total-byte cap.
MCP applies its existing JSON byte cap afterwards. Page totals describe the
matched snapshot before that presentation cap. They are not a freshness guarantee.
Device and group lists can be cached for 60 seconds; group membership and ARP
observations are fetched separately, so a result is not an atomic server snapshot.
The result has no connection generation, fetched-at, stale, partial-source or
safe failure metadata.

Redaction removes recognised secret patterns. It does not anonymise hostnames,
IP/MAC addresses, locations, device IDs or topology. Matching still uses raw text.
A secret-bearing query can therefore reveal existence through counts, ordering
or an empty/non-empty page even when returned text is redacted. Do not expose
this executor as a privacy boundary or to callers with less authority than the
captured LibreNMS account. Group ambiguity exceptions can contain raw candidate
names in Details. Treat all exception objects and their details as sensitive.

The factory accepts an optional Redactor and otherwise creates the default.
MCP reuses ToolRunner's supplied redactor. The internal constructor supports
existing MCP caches; public callers use the isolated factory. Neither path can
verify that the captured client represents one immutable connection. Hosts must
discard the whole instance when the connection changes. Cancellation is
cooperative; checks cannot make a concurrent connection switch atomic.

A safe typed exception/result contract is deferred. Replacing propagated failures
with a catch-all here would hide caller cancellation and diagnostic distinctions,
and would change MCP's established error mapping. Mobile must supply its own
fixed error codes/messages without forwarding ToolException Details or transport
exception messages. MCP's current ToolRunner still logs exception objects and can
return redacted server details; its policy is not a safe mobile logging contract.
