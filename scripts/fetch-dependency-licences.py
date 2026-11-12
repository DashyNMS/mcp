"""Write licence supplements for restored packages that embed no licence text.

Usage: fetch-dependency-licences.py <id>/<version> [<id>/<version> ...]

Most Microsoft, MCP SDK and Core packages declare only an SPDX expression, so
the collector cannot retain their actual terms from the archive. This fetches the
licence and third-party notices from the exact upstream commit each package's
nuspec records, so the evidence is tied to the source that built those bytes
rather than to a branch that may later change. Review the resulting diff before
committing it; the collector re-verifies every hash and the commit binding.

DashyNMS packages download from GitHub Packages with DASHYNMS_PACKAGES_TOKEN;
everything else comes from nuget.org.
"""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

# Upstream legal files per source repository. The first entry is the licence;
# the rest are notices. dotnet/dotnet is the .NET VMR: its packages here all come
# from src/runtime, and other components carry different files, so those fail.
SOURCES = {
    'https://github.com/dotnet/dotnet': ['src/runtime/LICENSE.TXT', 'src/runtime/THIRD-PARTY-NOTICES.TXT'],
    'https://github.com/dotnet/runtime': ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'],
    'https://github.com/dotnet/extensions': ['LICENSE', 'THIRD-PARTY-NOTICES.TXT'],
    'https://github.com/modelcontextprotocol/csharp-sdk': ['LICENSE', 'THIRD-PARTY-NOTICES.txt'],
    'https://github.com/DashyNMS/core': ['LICENSE'],
}
RUNTIME_PREFIXES = ('microsoft.extensions.', 'system.')
supplements = Path(__file__).resolve().parents[1] / 'docs/dependency-licences'


def download(url, token=None):
    request = urllib.request.Request(url)
    if token:
        # Unredirected: GitHub Packages redirects to signed storage, which rejects it.
        request.add_unredirected_header('Authorization', 'Basic ' + base64.b64encode(f'x:{token}'.encode()).decode())
    with urllib.request.urlopen(request) as response:
        return response.read()


for argument in sys.argv[1:]:
    package_id, version = (part.lower() for part in argument.split('/'))
    if package_id.startswith('dashynms.'):
        url = f'https://nuget.pkg.github.com/DashyNMS/download/{package_id}/{version}/{package_id}.{version}.nupkg'
        archive_bytes = download(url, os.environ['DASHYNMS_PACKAGES_TOKEN'])
    else:
        url = f'https://api.nuget.org/v3-flatcontainer/{package_id}/{version}/{package_id}.{version}.nupkg'
        archive_bytes = download(url)
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        nuspec = next(name for name in archive.namelist() if name.endswith('.nuspec'))
        metadata = ET.fromstring(archive.read(nuspec))
        embedded = {name.rsplit('/', 1)[-1].lower() for name in archive.namelist()}
    for element in metadata.iter():
        element.tag = element.tag.rsplit('}', 1)[-1]
    repository = metadata.find('./metadata/repository')
    if repository is None or not repository.get('commit'):
        raise SystemExit(f'{argument}: nuspec records no source commit; review manually')
    repository_url, commit = repository.get('url').removesuffix('.git'), repository.get('commit')
    if repository_url not in SOURCES:
        raise SystemExit(f'{argument}: no reviewed legal-file mapping for {repository_url}')
    if repository_url == 'https://github.com/dotnet/dotnet' and not package_id.startswith(RUNTIME_PREFIXES):
        raise SystemExit(f'{argument}: VMR package outside src/runtime; review its component manually')
    destination = supplements / package_id / version
    destination.mkdir(parents=True, exist_ok=True)
    files = {}
    owner_repo = repository_url.removeprefix('https://github.com/')
    for index, path in enumerate(SOURCES[repository_url]):
        source = f'https://raw.githubusercontent.com/{owner_repo}/{commit}/{path}'
        name = path.rsplit('/', 1)[-1]
        # The collector already retains notices the archive embeds verbatim.
        if index > 0 and name.lower() in embedded:
            continue
        data = download(source)
        (destination / name).write_bytes(data)
        files[name] = {'url': source, 'sha256': hashlib.sha256(data).hexdigest(),
                       'role': 'licence' if index == 0 else 'notice'}
    evidence = {'package': f'{package_id}/{version}', 'packageSha256': hashlib.sha256(archive_bytes).hexdigest(),
                'upstreamRepository': repository_url, 'upstreamRevision': commit, 'files': files}
    (destination / 'provenance.json').write_text(json.dumps(evidence, indent=2) + '\n')
    print(f'{argument}: {", ".join(files)} from {owner_repo}@{commit[:12]}')
