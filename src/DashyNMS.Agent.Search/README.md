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

Results are redacted but remain untrusted network data. Redaction does not hide
network identity or topology. Hosts must apply their own authorisation, presentation
limits, sanitisation, deadlines and evidence ownership checks.

## Consumption status

Publication is disabled; a reviewed workflow change is required to enable it. CI artefacts use `0.1.0-ci.<run-id>.<attempt>` and embed the repository commit.
`0.1.0-dev` identifies local packs and must not be used as a shipping pin.
Pull requests verify packages without publishing.
Run/attempt versions are unique, not immutable feed storage. Pin an exact verified
version; configure package-source mapping and grant the consumer repository read
access before using its `GITHUB_TOKEN` with `packages: read`.

Core remains at `2.0.0-ci.34`; mobile currently uses `2.0.0-ci.64`. NuGet's
minimum dependency range permits unification, but that is not compatibility proof.
Do not add a shipping mobile reference until the Core baseline has been reconciled
and mobile CI has verified restore, behaviour and Release trimming/AOT/runtime.
The package smoke check is plain .NET only.

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
