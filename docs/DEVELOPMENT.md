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

`src/DashyNMS.Agent.Search` contains typed device search and its dependency closure:
request/result records, directory/scope caches, device summaries, paging, redaction,
options and argument exceptions. It references Core, with no MCP or Agent adapter
reference. Existing namespaces remain unchanged; Agent forwards moved public types.
Options remain together to preserve existing signatures; this does not expose any
configuration, log, health or other tool through Search. MCP discovery, JSON shaping,
byte caps and ToolRunner remain in Agent. The solution and Docker restore include
Search. No Core pin or mobile code changes are part of this step for mobile #596.

MCP continues calling `DeviceSearch.FindDevicesAsync`, using its shared directory,
scope, options and redactor. It retains brief/verbose output, MAC observations,
ranking, filters, paging, cancellation and ToolRunner error handling. This lower-level
API propagates sensitive exceptions and is intended for adapters with a reviewed
error policy. It is not the safe host boundary.

Other hosts should reference **Search only** and use:

```csharp
using var search = HostDeviceSearch.Create(capturedClient, hostClock,
    serverTimestampsAreUtc: capturedTimestampSetting);
var outcome = await search.FindDevicesAsync(new FindDevicesRequest(Query: query), token);
// outcome.Result is a redacted success (possibly empty); otherwise outcome.Error
// contains a fixed DeviceSearchFailure code and fixed message.
```

The factory rejects missing client/clock and creates new caches without registering
services, reading environment variables or constructing transports. Supply a client
bound to one immutable connection generation, using host credentials, trust and
failover. Dispose the executor on switch/sign-out/settings changes. Disposal cancels
in-flight waits and refuses future calls, including warm-cache calls. It does not
dispose the client. Hosts must still discard results/evidence if their generation
has changed after a call returns; cancellation cannot make that transition atomic.
Never pass a mutable session client whose requests can switch to another connection.

`HostDeviceSearch` does not log or return exception objects, server messages, bodies,
URLs or ambiguity candidates. Invalid/ambiguous requests, refusal, not found, timeout,
unavailable source and unexpected failures have fixed codes/messages. Cancellation
of the caller or generation propagates as OperationCanceledException. An unrelated
transport cancellation maps to Timeout. A host-supplied deadline token also cancels;
the host distinguishes deadline expiry from user cancellation using its own token
sources. Hosts may log only reviewed metadata (tool, code, duration, generation).
No partial success is returned: required-source failure makes the whole search fail.

Caller cancellation reaches APIs and semaphore waits. Late cancelled responses are
not cached or returned. The host boundary also bounds its wait with WaitAsync;
late executor faults are observed and discarded without logging. An uncooperative
request can continue holding a cache semaphore until it finishes. Bound each call
with a deadline; if a cancelled transport does not finish, retire that generation
and create a new connection-bound instance before retrying.
Retiring the generation prevents new work through this boundary, but cannot stop
an already-issued request on a transport that ignores cancellation.

Results redact recognised secret patterns before either formatter sees them.
They remain untrusted network data: hosts must apply marker/control sanitisation,
presentation budgets and evidence ownership checks. Redaction does not anonymise
names, addresses or topology; raw matching/counts can disclose existence. Do not
use search as an authorisation boundary. Typed rows have a 50-match cap but no
string/byte cap. MCP adds its existing JSON cap. ARP observations can be numerous.
Device/group lists cache for 60 seconds, while membership/ARP use separate calls;
results have no freshness metadata and are not an atomic snapshot.

### Verification and publication follow-up

No builds/tests were run locally for this step, as requested. CI must run the whole
solution. `DeviceSearchTests` retains synthetic transport parity and independent
MCP field expectations, ranking/paging, scope/MAC, redaction, cache isolation,
semaphore cancellation and late-response coverage. `HostDeviceSearchTests` in the
Search-only test project covers safe server/transport failures, invalid versus
empty results, retirement of uncooperative requests, caller cancellation and the
absence of direct MCP/adapter assembly references. It also covers API timeout and
admin-only flags, transport argument failures, cancellation/completion races, late
fault observation, retirement and MAC empty/404 outcomes. The observer test awaits
its fault-only continuation; it does not force GC or assert process-wide unobserved
exception events. These are test additions, not passing
results. The existing CI solution commands pick up the new project.

The assembly-reference test inspects only `GetReferencedAssemblies()` on the Search
assembly. It does not traverse Core or other dependencies, inspect resolved NuGet
assets/native libraries, detect dynamically loaded assemblies, or validate trimming
and AOT. It is a direct-reference guard, not a complete dependency graph audit.

Before publication, verify source and binary compatibility (including type forwards),
MCP schema discovery and the complete resolved dependency graph. Search still uses
Core ci.34; mobile uses ci.64. Reconcile and prove compatibility upstream rather than
downgrading mobile. No new package has been packed or published. Establish immutable
Search/Agent versions, provenance, feed mapping, mobile CI package read access and
version policy before adding any shipping mobile reference. Audit and publish the
shared synthetic fixture bundle separately; this step does not publish fixtures.

Static mobile dispatch/schema and generated serialisation, freshness metadata,
compact formatting/evidence, matched cross-adapter fixtures, mobile session races,
Release iOS trimming/AOT, simulator/device execution, Android Release runtime and
size/token/performance measurements remain unverified follow-ups. MCP-free source
references alone do not prove the transitive graph or mobile runtime compatibility.
The other tools, mobile's existing lookups and Diagnostics remain deferred.
