# DashyNMS.Agent.Shared

Typed, read-only LibreNMS operations over a host-owned `DashyNMS.Core` client, shared
by the DashyNMS MCP server and the DashyNMS apps. It holds the typed operations behind every read-only MCP network tool and the
shared tool catalogue. MCP retains protocol discovery, formatting and byte caps. It replaces
`DashyNMS.Agent.Search` (`0.1.0-ci.*`), which is no longer published.
Targets .NET 10. Reference this package rather than `DashyNMS.Agent` (the MCP
adapter). The direct dependency is Core; Core's own dependencies remain transitive.
Namespaces retain the `DashyNMS.Agent` prefix for source compatibility.

Use `DashyNMS.Agent.Devices.HostDeviceSearch.Create` with a client captured for
one immutable connection generation and a host clock. Dispose the search when
switching connection or signing out; the host must also discard stale results.
The factory does not own or dispose the client. See the repository developer guide
for the full boundary, cancellation and error contract.

`DashyNMS.Agent.Alerts.HostOpenAlerts` lists the alerts open now as device and rule groups
under the same rules: one per captured connection, disposed on a switch, fixed `HostFailure`
categories. Each group's representative is a source-qualified `AlertKey` (`fault:12` or
`alert:12`), never a bare number.

`DashyNMS.Agent.Devices.HostDeviceSummary` reads one device by id from `devices/{id}` under the
same rules: the redacted `DeviceSummary` with its `State`, uptime (null when none is reported) and
the location's id, redacted name and exact LibreNMS `FilterName`. The filter name is neither trimmed
nor redacted, so a host's device filter can match on it; never show it or give it to a model. A
device the server doesn't return is `NotFound`, never a device-list row. MCP's `device_health`
builds its device and uptime from the same read and shaping.

`DashyNMS.Agent.Health.HostDeviceSensors` reads one device's sensors, judged as MCP's
`device_sensors` judges them, under the same rules. It needs the host's transport as well as
its client, both bound to the captured connection, for the per-device health routes used when
the account may not read the network-wide sensor list. A missing reading stays null, and each
reading says what its state was judged against, so an unassessed one is never shown as healthy.

`DashyNMS.Agent.Devices.HostDeviceResolver` resolves the device a model names (an ID,
hostname, display name, sysName or IP address) in MCP's tiers, under the same rules. The result is
`Found` with the redacted device, `Ambiguous` with up to five redacted candidates and the rest
counted, or `NoMatch`. A device list that can't be read is a `HostFailure`, never no match.

`DashyNMS.Agent.Catalogue.ToolCatalogue` lists every MCP tool with its name, title, description
and parameters (type, required, default, documented values or range, description), and the other
tools each description names. MCP's own attributes read the same text, so a host describing a tool
from the catalogue tells its model exactly what MCP tells a client. Leave out what a host doesn't
support; don't reword it.

Each `DeviceSummary` keeps two views of a device's state. `Status` is MCP's word
(`up`, `down` or `disabled`, also the status filter), read from Core's compatibility
flags, so an omitted flag reads as down and an ignored device as up or down. `State`
is Core's `ReportedState`: `Unknown` when LibreNMS left a flag out, and `Ignored`
on its own. Show `State`. Search checks maintenance only for returned rows. Active maintenance sets both
views to maintenance; a failed check preserves the reported state and leaves
`UnderMaintenance` null, rather than claiming it is off. Other device summaries
do not request maintenance. `State` is left out of JSON.

Results are redacted but remain untrusted network data. Redaction does not hide
network identity or topology. Hosts must apply their own authorisation, presentation
limits, sanitisation, deadlines and evidence ownership checks.

## Consumption status

Each verified push to `main` in DashyNMS/mcp publishes `2.0.0-ci.<run-id>.<attempt>`
to GitHub Packages (`https://nuget.pkg.github.com/DashyNMS/index.json`), with a GitHub
build provenance attestation and the repository commit in the nuspec. Pull requests
verify packages without publishing. `2.0.0-dev` identifies local packs and must not
be used as a shipping pin.

The feed refuses a second push of a version and the workflow never overwrites one,
but an organisation administrator can still delete a version. Pin an exact version,
check it with `gh attestation verify <nupkg> --repo DashyNMS/mcp`, and use a NuGet
lock file so restore fails if the content changes. Map `DashyNMS.*` to the GitHub
feed and restore with a token that has `packages: read`.

The package is built against Core `2.0.0-ci.64` (MCP's pin, and mobile's) and
declares it as a minimum. Before publication, CI runs the Shared tests on the exact
packed binary with that Core. That is plain .NET
evidence: a consumer must still verify its own restore, Release trimming/AOT and
runtime behaviour in its CI.

## Licence

This Shared library and its package README are MIT licensed, copyright (c) 2026
Thomas Pickup. Include the bundled LICENSE when redistributing it with mobile.
The project-owned MCP host, Agent adapter and other repository files are also MIT
licensed. Dependencies retain their own licences. See
[notices](https://github.com/DashyNMS/mcp/blob/main/NOTICES.md) for provenance and
third-party rights limitations.

A stable Shared release also needs a verified stable Core dependency. The current
prerelease Core baseline is for CI packages and can trigger NuGet warning NU5104
if used by a stable package.

Source debugging is unverified. The SDK may add SourceLink implicitly, but this
pipeline does not inspect PDB mappings or produce a symbol package.

The package includes LICENSE and NOTICES.md for offline access. MIT applies to
project-owned material; dependency licences and attribution remain applicable.

`DashyNMS.Agent.Overview.HostNetworkOverview` shares the MCP overview: required device
counts, optional alert/port/system sources, fixed failure categories, and bounded
redacted alert groups, down devices and down locations with explicit omitted counts.
Dispose it on retirement and supply a host deadline, as for search and open alerts.

Batch 5 host boundaries also cover complete device health, group/location names,
alert details and rules, port status/neighbours/tracing, device detail sections,
fleet rankings and health, historical events/changes/alerts, Graylog search,
Unimus config tools and graphs. See the developer guide for the factory and
request/result types and each operation's endpoint contract. Optional service
clients, config-text policy, threshold choices and certificate trust come from
the host. The shared package never reads an app's settings or environment.

Host sensor options use Core's threshold evaluator. Factories capture mutable
threshold choices before reads; create a new lookup set for a later question
when settings change. The default settings preserve the earlier assessment.
Graph results use Core's image byte limit and return PNG bytes separately from
metadata. Image pixels are not redacted. Hosts must disclose source failures,
coverage gaps, sampling and omissions when compacting results, and must decide
whether their model can receive an image before passing it through.
