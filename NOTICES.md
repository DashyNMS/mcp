# Licensing and attribution

Project-owned source code, designs, graphics and text are licensed under the
MIT licence in LICENSE, copyright (c) 2026 Thomas Pickup. Third-party materials retain their own terms.

"DashyNMS" and the DashyNMS logo/app icon are trademarks of Thomas Pickup.
The MIT licence permits use of project-owned artwork under copyright, but grants
no trademark rights. Truthful reference to DashyNMS is permitted; use of the marks
must not imply endorsement or identify another product or service.

DashyNMS.Core is consumed as a NuGet package from DashyNMS/core, currently
pinned in Directory.Build.props. It retains its own licence and attribution.
NuGet dependencies,
the .NET runtime, and Microsoft/Canonical container base layers retain their
publishers' licences. Include their applicable notices when redistributing them.

## Container distribution

Images built from this revision onwards are labelled MIT for project-owned
material only. Each image carries this file and LICENSE in /app, and
/app/third-party holds the licence and notice files for every restored NuGet
package, copied verbatim, with an inventory of package and file hashes. Where a
package embeds no licence text, the text comes from the exact upstream commit its
nuspec records; see docs/dependency-licences. The .NET runtime and Canonical
chiseled base layers are not covered by that inventory and keep their
publishers' terms. Images published before this change carry the earlier
PolyForm Internal Use labels and no bundled notices.

## Search package

DashyNMS.Agent.Search bundles LICENSE and this file. It contains no dependency
binaries: consumers restore DashyNMS.Core and its dependencies under their own
terms.

## Logo provenance

The server icon was added at MCP commit
3c3a882ff3dcc2d0d8b278e46162018a546afcae. That commit identifies it as
the DashyNMS logo but records no upstream asset revision or copyright grant.
The website added assets/img/favicon.svg at commit
941b24d59672c1e981a47b30081832720e838b2f, authored by Thomas Pickup:
https://github.com/DashyNMS/website/commit/941b24d59672c1e981a47b30081832720e838b2f
Its circle and pulse-line geometry matches the mobile sign-in SVG. The commit
and SVG contain no asset-specific licence or earlier-source attribution.
This establishes a website source for the mobile geometry, not a verified
source for the MCP PNG or proof of original authorship. Earlier third-party
origins remain unverified. Project ownership and trademark statements do not establish rights in
underlying third-party artwork. Any applicable upstream attribution must be
retained; no additional upstream author or licence is asserted here.
