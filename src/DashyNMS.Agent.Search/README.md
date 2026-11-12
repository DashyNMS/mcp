# DashyNMS.Agent.Search

Typed, read-only LibreNMS device search over a host-owned `DashyNMS.Core` client.
Targets .NET 10. Reference this package rather than `DashyNMS.Agent` (the MCP
adapter). The direct dependency is Core; Core's own dependencies remain transitive.
Namespaces retain the `DashyNMS.Agent` prefix for source compatibility.

Use `DashyNMS.Agent.Devices.HostDeviceSearch.Create` with a client captured for
one immutable connection generation and a host clock. Dispose the search when
switching connection or signing out; the host must also discard stale results.
The factory does not own or dispose the client. See the repository developer guide
for the full boundary, cancellation and error contract.

Each `DeviceSummary` keeps two views of a device's state. `Status` is MCP's word
(`up`, `down` or `disabled`, also the status filter), read from Core's compatibility
flags, so an omitted flag reads as down and an ignored device as up or down. `State`
is Core's `ReportedState`: `Unknown` when LibreNMS left a flag out, and `Ignored`
on its own. Show `State`. Maintenance needs a per-device call and is never set. `State`
is left out of JSON.

Results are redacted but remain untrusted network data. Redaction does not hide
network identity or topology. Hosts must apply their own authorisation, presentation
limits, sanitisation, deadlines and evidence ownership checks.

## Consumption status

Each verified push to `main` in DashyNMS/mcp publishes `0.1.0-ci.<run-id>.<attempt>`
to GitHub Packages (`https://nuget.pkg.github.com/DashyNMS/index.json`), with a GitHub
build provenance attestation and the repository commit in the nuspec. Pull requests
verify packages without publishing. `0.1.0-dev` identifies local packs and must not
be used as a shipping pin.

The feed refuses a second push of a version and the workflow never overwrites one,
but an organisation administrator can still delete a version. Pin an exact version,
check it with `gh attestation verify <nupkg> --repo DashyNMS/mcp`, and use a NuGet
lock file so restore fails if the content changes. Map `DashyNMS.*` to the GitHub
feed and restore with a token that has `packages: read`.

The package is built against Core `2.0.0-ci.64` (MCP's pin, and mobile's) and
declares it as a minimum. Before publication, CI runs the Search tests on the exact
packed binary with that Core. That is plain .NET
evidence: a consumer must still verify its own restore, Release trimming/AOT and
runtime behaviour in its CI.

## Licence

This Search library and its package README are MIT licensed, copyright (c) 2026
Thomas Pickup. Include the bundled LICENSE when redistributing it with mobile.
The project-owned MCP host, Agent adapter and other repository files are also MIT
licensed. Dependencies retain their own licences. See
[notices](https://github.com/DashyNMS/mcp/blob/main/NOTICES.md) for provenance and
third-party rights limitations.

A stable Search release also needs a verified stable Core dependency. The current
prerelease Core baseline is for CI packages and can trigger NuGet warning NU5104
if used by a stable package.

Source debugging is unverified. The SDK may add SourceLink implicitly, but this
pipeline does not inspect PDB mappings or produce a symbol package.

The package includes LICENSE and NOTICES.md for offline access. MIT applies to
project-owned material; dependency licences and attribution remain applicable.
