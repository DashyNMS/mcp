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

`src/DashyNMS.Agent.Shared` contains typed host operations (device search first) and their dependency closure:
request/result records, directory/scope caches, device summaries, paging, redaction,
options and argument exceptions. It references Core, with no MCP or Agent adapter
reference. Existing namespaces remain unchanged; Agent forwards moved public types.
Options remain together to preserve existing signatures; this does not expose any
configuration, log, health or other tool through the Shared package. MCP discovery, JSON shaping,
byte caps and ToolRunner remain in Agent. The solution and Docker restore include
the Shared package. No Core pin or mobile code changes are part of this step for mobile #596.

MCP continues calling `DeviceSearch.FindDevicesAsync`, using its shared directory,
scope, options and redactor. It retains brief/verbose output, MAC observations,
ranking, filters, paging, cancellation and ToolRunner error handling. This lower-level
API propagates sensitive exceptions and is intended for adapters with a reviewed
error policy. It is not the safe host boundary.

Other hosts should reference **the Shared package only** and use:

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

### Open alerts for host-owned clients

`OpenAlerts` (namespace `DashyNMS.Agent.Alerts`) is the alerts open now as typed groups, one
per rule firing on a device, from Core's `AlertGrouping`. `active_alerts` builds its open view
on it, so MCP and other hosts share grouping, worst-first order, filters, paging and totals; MCP
keeps its JSON shape, brief/verbose fault lists and byte cap. Each `OpenAlertGroup` carries:

- its device and rule key (`AlertGroupKey`), a type of its own so it is never taken for a
  fault or alert id;
- a source-qualified representative (`AlertKey`: `fault:12` or `alert:12`), the first
  unacknowledged fault, so a fault and a legacy alert with the same number stay distinct;
- severity, an active, acknowledged or partly acknowledged state, fault and acknowledged
  counts, up to 25 faults with their own keys and a count of the rest;
- the hostname and redacted `DeviceSummary`.

Recovered rows are dropped even if the server returns them, as `OpenAlertTotals` already
did, so a recovered fault never keeps its group open. Hosts resolve a device to its id
themselves. `HostOpenAlerts` is the safe boundary, shared with `HostDeviceSearch` through
`HostBoundary`: fixed `HostFailure` categories and messages, no server text, cancellation
that wins over late results, and no partial success. If the device list or alerts can't be
read, the call fails rather than reporting no alerts. `Fixtures/OpenAlerts` holds the shared
synthetic fixture, and `SharedOpenAlertsHttpFixtureTests` serves it over loopback HTTP to
both adapters.

### Verification and publication follow-up

No builds/tests were run locally for this step, as requested. CI must run the whole
solution. `DeviceSearchTests` retains synthetic transport parity and independent
MCP field expectations, ranking/paging, scope/MAC, redaction, cache isolation,
semaphore cancellation and late-response coverage. `HostDeviceSearchTests` in the
Shared-only test project covers safe server/transport failures, invalid versus
empty results, retirement of uncooperative requests, caller cancellation and the
absence of direct MCP/adapter assembly references. It also covers API timeout and
admin-only flags, transport argument failures, cancellation/completion races, late
fault observation, retirement and MAC empty/404 outcomes. The observer test awaits
its fault-only continuation; it does not force GC or assert process-wide unobserved
exception events. These are test additions, not passing
results. The existing CI solution commands pick up the new project.

The assembly-reference test inspects only `GetReferencedAssemblies()` on the Shared
assembly. It does not traverse Core or other dependencies, inspect resolved NuGet
assets/native libraries, detect dynamically loaded assemblies, or validate trimming
and AOT. It is a direct-reference guard, not a complete dependency graph audit.

Only the Shared package is published (see below). Agent's type forwards and MCP schema discovery
are not covered by that pipeline and need their own verification before any Agent
package is published. Audit and publish the shared synthetic fixture bundle
separately; this pipeline does not publish fixtures.

Static mobile dispatch/schema and generated serialisation, freshness metadata,
compact formatting/evidence, matched cross-adapter fixtures, mobile session races,
Release iOS trimming/AOT, simulator/device execution, Android Release runtime and
size/token/performance measurements remain unverified follow-ups. MCP-free source
references alone do not prove the transitive graph or mobile runtime compatibility.
The other tools, mobile's existing lookups and Diagnostics remain deferred.

### Shared package for mobile

`shared-package.yml` now defines a separate Shared package pipeline. It builds and
runs solution tests, packs **the Shared package only**, inspects its metadata/licence/commit and
restores, builds and executes a minimal package-only .NET 10 consumer in a fresh
NuGet package directory. The exact Shared ID maps to the local packed feed;
`DashyNMS.*` dependencies still map to GitHub Packages. The smoke check rejects
Agent/MCP packages and project dependencies, checks the resolved Core baseline,
and records the complete resolved library list and package SHA-256 in
`shared-provenance.json`. This is an inventory and direct boundary check, not
proof of mobile runtime compatibility or of the absence of dynamic loading.

The package was published as `DashyNMS.Agent.Search` (`0.1.0-ci.*`) until it was
renamed so every shared host operation, not only device search, ships in one
package that hosts pin and verify once. Those versions stay on the feed for pins
that still name them; nothing new is published under the old ID. The new ID
starts at 2.0.0, in line with Core 2.0. Its first publish fails the visibility
check until an organisation owner makes it public (see Publication).

Package identity is `DashyNMS.Agent.Shared`, independently versioned from the MCP
host, `2.0.0-ci.<GitHub run ID>.<run attempt>`. `2.0.0-dev` is for local
packs only. The Shared package's Version and PackageVersion share the same value; the SDK derives
assembly and informational versions from it. CI builds with that value and repository
commit, tests without rebuilding, then packs without building or restoring. The smoke
check compares packed, producer and clean-consumer DLL bytes and checks assembly
metadata. The verify job then publishes the package's SHA-256 as a job output; later
jobs compare their downloads with that output, not with the report inside the artefact.

The Shared package is MIT licensed, copyright (c) 2026 Thomas Pickup, like the rest of the
repository. The package declares the `MIT` expression and bundles its own
`LICENSE` (identical to the repository's) and `NOTICES.md`; the check fails if
either drifts. Core and its dependencies are restored by the consumer under their
own terms, and the smoke consumer collects their licence evidence the same way
the image build does (see `docs/dependency-licences`).

#### Core compatibility

The Shared package is compiled against MCP's Core pin (`DashyNMSCoreVersion`), and its package
declares that as a minimum, so a host that pins a newer Core gets that one instead.
The `core-compatibility` job checks each Core a consumer actually pins. MCP's own
and mobile's (the version in mobile's `DashyNMS.Mobile.Shared.csproj`) are both
`2.0.0-ci.64`, so there is one entry. For each it:

- builds the Shared package from source against that Core with warnings as errors (source
  compatibility, for a future producer pin), and
- restores the **downloaded package** into the Shared-only test project in a fresh
  package directory beside an exact pin of that Core, checks the resolved graph has
  one Core at that version and no Agent/MCP packages, compares the restored DLL with
  the verified package, and runs `DashyNMS.Agent.Shared.Tests` on that combination
  (`scripts/verify-shared-core-compatibility.py`). This is the binary a host runs.

When mobile moves Core, update its matrix entry first; a failing entry blocks
publication. Moving MCP's own pin can change alert and tool behaviour well beyond
search, so each move needs its own review of what the Core range changes for MCP.
The move from `2.0.0-ci.34` to `2.0.0-ci.64` (for the reported device state) was
reviewed that way: MCP calls none of the changed alert actions or graph legends, the
new transport members default or pass through, and Core's compatibility flags still
read an omitted value as false. The ranking tests in
`DeviceSearchTests` live in the Agent test project and run against MCP's pin only;
the Shared-only tests cover the host boundary, failures and cancellation. This is
plain .NET evidence, not mobile trimming/AOT, Android runtime or device behaviour.

#### Publication

Every push to protected `main` whose `verify` and `core-compatibility` jobs pass runs
the `publish` job; pull requests never reach it. It holds the only write token
(`packages: write`, plus `id-token`/`attestations: write` for the attestation) and:

1. downloads the package from the same run and runs
   `scripts/verify-shared-provenance.py --for-publication`, with the expected source
   values taken from the run's own Actions context, then checks the SHA-256 against
   the verify job's output;
2. creates a GitHub build provenance attestation for the exact `.nupkg`
   (`gh attestation verify <file> --repo DashyNMS/mcp` checks it);
3. pushes it to `https://nuget.pkg.github.com/DashyNMS` **without**
   `--skip-duplicate`, so an existing version fails the job rather than being
   reported as published;
4. downloads it back through the feed and fails unless the bytes match, then checks
   the package is public and linked to `DashyNMS/mcp` (`scripts/verify-published-shared.py`).
   The token is only ever sent over HTTPS to `nuget.pkg.github.com` and
   `api.github.com`, whatever download URL the feed's service index advertises;
   storage redirects are followed without it.

A run's version includes its attempt number, and the verify job's attempt fixes it.
Re-running only the failed publish job therefore fails provenance verification on
purpose; re-run all jobs instead, which produces a new version.

What this does and does not guarantee:

- GitHub Packages rejects a second push of an existing version, and this workflow
  never deletes or overwrites one. That is feed behaviour, not storage immutability:
  an organisation package administrator can still delete a version. Consumers should
  pin the exact version and verify its hash or attestation; a NuGet lock file
  (`RestorePackagesWithLockFile`) records the content hash and fails on change.
- The attestation binds the package digest to this workflow, commit and run. It does
  not prove review quality; the protection flag reflects Actions context, not an
  audit of the branch rules.
- The package is linked to this public repository, so it should be public, like
  `DashyNMS.Core`, and mobile's `GITHUB_TOKEN` with `packages: read` can restore it.
  New GitHub packages can start private, and GitHub has no API to change a
  package's visibility, so the workflow cannot fix it. If the first publish job
  fails on visibility, an organisation owner sets `DashyNMS.Agent.Shared` to public
  in its package settings (this cannot be undone), then re-runs all jobs or merges
  again, which publishes a new version that passes. Do not pin a version whose
  publish job failed, including the private first one, which becomes visible when
  the package turns public.
- The visibility check fails closed. GitHub documents the packages REST API for
  classic personal access tokens only, so the Actions token may be refused package
  metadata. Then the job checks anonymously instead: a private package's page is a
  404 to a signed-out visitor, and a public linked package's page redirects to
  `github.com/DashyNMS/mcp/pkgs/nuget/DashyNMS.Agent.Shared`, as Core's does to
  `DashyNMS/core`. That relies on GitHub's web routes rather than a documented API;
  any other answer fails the job. A mobile restore is still the final check.

The smoke consumer references the actual Core ILibreNmsClient type used by the Shared package's
factory, traverses resolved package dependencies and rejects Agent/MCP dependencies.
It verifies the nuspec dependency group, MIT licence expression, readme declaration and content,
and allows only the expected Shared DLL under package asset directories. This checks
the resolved .NET graph, not dynamic loading, dependency safety or mobile behaviour.
Source embedding was removed. This project adds no explicit SourceLink package or
symbol-package pipeline. The .NET SDK may supply SourceLink support implicitly;
this check does not inspect PDB source mappings or prove source debugging works.
Repository metadata alone does not establish that support.

The Core dependency check accepts NuGet's equivalent bare minimum version and
`[version, )` range encodings, while the clean restore must resolve the pinned
baseline exactly. The archive check allows only known packaging metadata, licence, notices,
readme and the Shared DLL; unexpected files and duplicate entries fail. Expected
CLR assembly version comes from `SHARED_VERSION`, rather than a fixed version.
A future stable Shared release must first choose and verify a stable Core baseline.
The current prerelease Core dependency is intentional for CI packages and may
produce NU5104 when packing a stable Shared version. Do not suppress that warning
or declare a stable release ready while the dependency remains a prerelease.

Before mobile pins a version, confirm in mobile CI that its existing `packages: read`
token restores that exact published version through mobile's `DashyNMS.*` feed
mapping. MCP's own restore cannot prove mobile's access. Never ship a local checkout
reference.

Still outstanding for mobile #596: source/binary compatibility of Agent type
forwards, schema parity, mobile generation races, the shared fixture bundle,
Release iOS trimming/AOT and Android runtime/performance. Keep mobile #596 open.
Mobile redistribution must include the Shared package's LICENSE and the licence texts of Core
and its dependencies.

All builds, tests, pack/restore, compatibility and publication run in CI only.

### Shared network overview

`NetworkOverview` in Agent.Shared owns overview counts, optional-source failures and
bounded detail selection for MCP and mobile (DashyNMS/mobile#600). MCP retains its
JSON field names; in-process hosts consume `HostNetworkOverview` typed results.
Devices remain required. Alerts, ports and system failures retain other sources
and expose only fixed categories. Alert row shaping is shared with `OpenAlerts`.
The NetworkOverview synthetic fixtures cover a complete and a refused-ports case.
