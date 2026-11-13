# Dependency licence evidence

The image build and the Shared package check run
`scripts/collect-dependency-notices.py` against the packages they actually
restored. It copies legal files verbatim from each archive and records the
package SHA-256, restore SHA-512, nuspec and file hashes in `inventory.json`
(`/app/third-party` in the image). It never substitutes a generic template for a
package's own copyright notice. Every restored package is included, build-only
ones too. Base-image licences are separate.

Most Microsoft, MCP SDK and Core packages declare only an SPDX expression and
embed no licence text, so they need a supplement here under
`<lowercase-package-id>/<version>/`. A supplement holds the licence (and any
upstream third-party notices the archive lacks) fetched from the commit the
package's own nuspec records, plus `provenance.json` with the package SHA-256,
that repository and commit, and each file's source URL, SHA-256 and role. The
collector rejects a supplement whose package hash, repository or commit differs
from the restored archive, so evidence cannot drift from the bytes it describes.
A notice alone does not count as a licence, and an MIT expression also needs the
actual MIT terms and a copyright line.

## When a dependency changes

A version bump (including from Dependabot) fails the image build and the Shared
check until the new versions have supplements. Generate them with:

```sh
python3 scripts/fetch-dependency-licences.py microsoft.extensions.hosting/10.0.14 ...
```

DashyNMS packages need `DASHYNMS_PACKAGES_TOKEN`. The script only knows the
upstream layout of repositories it has been reviewed against; add a mapping
after checking a new repository's licence and notice files. Review the diff,
in particular any licence text that changed, and delete supplements for versions
no longer restored.
