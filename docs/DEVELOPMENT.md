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

Before publication, verify source and binary compatibility (including type forwards)
and MCP schema discovery. Resolve the licence blocker described below and establish
a reviewed release policy. Audit and publish the shared synthetic fixture bundle
separately; this pipeline does not publish fixtures.

Static mobile dispatch/schema and generated serialisation, freshness metadata,
compact formatting/evidence, matched cross-adapter fixtures, mobile session races,
Release iOS trimming/AOT, simulator/device execution, Android Release runtime and
size/token/performance measurements remain unverified follow-ups. MCP-free source
references alone do not prove the transitive graph or mobile runtime compatibility.
The other tools, mobile's existing lookups and Diagnostics remain deferred.

### Search package preparation for mobile #596

`search-package.yml` now defines a separate Search package pipeline. It builds and
runs solution tests, packs **Search only**, inspects its metadata/licence/commit and
restores, builds and executes a minimal package-only .NET 10 consumer in a fresh
NuGet package directory. The exact Search ID maps to the local packed feed;
`DashyNMS.*` dependencies still map to GitHub Packages. The smoke check rejects
Agent/MCP packages and project dependencies, checks the resolved Core baseline,
and records the complete resolved library list and package SHA-256 in
`search-provenance.json`. This is an inventory and direct boundary check, not
proof of mobile runtime compatibility or of the absence of dynamic loading.

Package identity is `DashyNMS.Agent.Search`, independently versioned from the MCP
host, initially `0.1.0-ci.<GitHub run ID>.<run attempt>`. These versions are unique
per run/attempt, not immutable: feed administrators may delete packages, and this
workflow does not enforce feed retention. `0.1.0-dev` is for local packs only.
Search's Version and PackageVersion share the same value; the SDK derives assembly
and informational versions from it. CI builds with that value and repository commit,
tests without rebuilding, then packs without building or restoring. The smoke check
compares packed, producer and clean-consumer DLL bytes and checks assembly metadata.

Publication is disabled. This workflow has read-only permissions and no publication
job, push command or write token. PR and main CI still produce pack/restore evidence.
The repository PolyForm Internal Use licence blocks publication and mobile
redistribution pending explicit rights/licence resolution by the owner. Core's MIT
licence does not relicense Search. Including LICENSE is metadata, not permission.
The LICENSE notice still says Core comes from an `external/desktop` submodule and
is offered under the server licence. That notice is stale relative to today's
`DashyNMS.Core` package dependency. The owner must reconcile the notice and rights
before publication or redistribution. This preparation preserves LICENSE verbatim;
it cannot resolve ownership or relicense Core. Search copyright metadata records
`Copyright (c) 2026 Thomas Pickup`, matching the existing required notice.

Any future publication requires a separately reviewed workflow change after licence
resolution. Download the package and report from the verified run, then execute
`scripts/verify-search-provenance.py --for-publication` with that source run's
expected `SEARCH_VERSION` and `GITHUB_SHA` immediately before pushing the exact
named package. Supply `EXPECTED_SOURCE_EVENT_NAME`, `EXPECTED_SOURCE_REF`,
`EXPECTED_SOURCE_REF_PROTECTED`, `EXPECTED_SOURCE_RUN_ID`,
`EXPECTED_SOURCE_RUN_ATTEMPT` and `EXPECTED_SOURCE_REPOSITORY` from independently
trusted Actions run context, never by copying the downloaded report. These values
refer to the producing run, not a later publishing run. The report records the
source event, ref, protection flag, run ID/attempt and repository. Publication mode
requires a `push` on `refs/heads/main`, protection `true`, and `DashyNMS/mcp`.
It checks those expected values, nuspec ID/version/repository/commit, the single
package file set and SHA-256. Any mismatch must block publication. Normal CI
verification permits PR merge refs and their synthetic merge commits; PR artefacts
cannot pass publication mode. CI does not run publication mode, so a successful
main run does not establish publication eligibility. No publication path is provided.
The protection flag reflects Actions context, not an audit of protection rules or
review quality. The future publisher must verify the source run succeeded and its
workflow revision was reviewed. The unsigned sidecar
can detect mismatched bytes; it is not a signed attestation or protection against
replacement of both report and package. Review feed permissions and retention then.

The smoke consumer references the actual Core ILibreNmsClient type used by Search's
factory, traverses resolved package dependencies and rejects Agent/MCP dependencies.
It verifies the nuspec dependency group, file licence/readme declarations and content,
and allows only the expected Search DLL under package asset directories. This checks
the resolved .NET graph, not dynamic loading, dependency safety or mobile behaviour.
Source embedding was removed. This project adds no explicit SourceLink package or
symbol-package pipeline. The .NET SDK may supply SourceLink support implicitly;
this check does not inspect PDB source mappings or prove source debugging works.
Repository metadata alone does not establish that support.

The Core dependency check accepts NuGet's equivalent bare minimum version and
`[version, )` range encodings, while the clean restore must resolve the pinned
baseline exactly. The archive check allows only known packaging metadata, licence,
readme and the Search DLL; unexpected files and duplicate entries fail. Expected
CLR assembly version comes from `SEARCH_VERSION`, rather than a fixed 0.1.0.
A future stable Search release must first choose and verify a stable Core baseline.
The current prerelease Core dependency is intentional for CI packages and may
produce NU5104 when packing a stable Search version. Do not suppress that warning
or declare a stable release ready while the dependency remains a prerelease.

Before mobile consumption, grant the mobile repository Actions read access on the
new Search package and confirm its existing `packages: read` token can restore the
exact published version. MCP-token restore in this pipeline cannot prove mobile's
access. GitHub package access is external configuration, not set by this change.
Use mobile's existing `DashyNMS.*` feed mapping; never ship a local checkout reference.

**Core reconciliation remains blocked.** Source inventory confirms MCP ci.34 and
mobile ci.64; neither package metadata nor a larger version number proves API,
binary or semantic compatibility. Both pins remain unchanged. CI must first test
the candidate reconciled baseline across MCP and mobile, including alert semantics,
then update the producer baseline and consumer pin together. This smoke consumer
verifies ci.34 only. No mobile code or shipping dependency was added. Source/binary
compatibility of Agent type forwards, schema parity, mobile generation races,
fixtures, Release iOS trimming/AOT and Android runtime/performance remain follow-up
work. Keep mobile #596 open. The unchanged PolyForm licence also requires a rights
review before mobile redistribution; Core's MIT licence does not relicense Search.

All builds, tests, pack/restore and runtime verification are CI-only for this task.
None has been executed locally; workflow success and feed permissions remain
unverified until CI runs.

Before any future publication, rerun the complete package inspection and smoke
verification on the downloaded artefact; the provenance script alone checks only
identity and hash. Publication remains disabled.
