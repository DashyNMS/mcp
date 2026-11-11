"""Verify artefact identity; --for-publication additionally requires protected main.

The unsigned report is not an attestation. Expected source context must come from
an independently trusted Actions run, never from the downloaded report itself.
This does not authorise publication or resolve the licence blocker.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--for-publication', action='store_true')
args = parser.parse_args()
artifacts = Path(__file__).resolve().parents[1] / 'artifacts'
report = json.loads((artifacts / 'search-provenance.json').read_text())
version = os.environ['SEARCH_VERSION']
commit = os.environ['GITHUB_SHA']
# For downloaded artefacts these variables describe the SOURCE run, not the
# publication workflow. Require explicit expected values in publication mode.
fields = {'event': 'EVENT_NAME', 'ref': 'REF', 'refProtected': 'REF_PROTECTED',
          'runId': 'RUN_ID', 'runAttempt': 'RUN_ATTEMPT', 'repository': 'REPOSITORY'}
source = {key: os.environ[('EXPECTED_SOURCE_' if args.for_publication else 'GITHUB_') + suffix]
          for key, suffix in fields.items()}
if report['source'] != source or source['repository'] != 'DashyNMS/mcp':
    raise SystemExit('Provenance source does not match the expected Actions run')
if args.for_publication and (source['event'], source['ref'], source['refProtected']) != ('push', 'refs/heads/main', 'true'):
    raise SystemExit('Publication requires a push run on protected main')
expected = f'DashyNMS.Agent.Search.{version}.nupkg'
if (report['package'], report['version'], report['repositoryCommit']) != (expected, version, commit):
    raise SystemExit('Provenance identity does not match the expected workflow run')
packages = list(artifacts.glob('*.nupkg'))
if {p.name for p in artifacts.iterdir()} != {expected, 'search-provenance.json'} or len(packages) != 1 or packages[0].name != expected:
    raise SystemExit('Expected exactly the verified Search package')
if hashlib.sha256(packages[0].read_bytes()).hexdigest() != report['sha256']:
    raise SystemExit('Package SHA-256 does not match provenance')
with zipfile.ZipFile(packages[0]) as archive:
    ns = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
    if archive.namelist().count('DashyNMS.Agent.Search.nuspec') != 1:
        raise SystemExit('Expected one Search nuspec')
    metadata = ET.fromstring(archive.read('DashyNMS.Agent.Search.nuspec')).find('n:metadata', ns)
    repository = metadata.find('n:repository', ns)
    if (metadata.findtext('n:id', namespaces=ns), metadata.findtext('n:version', namespaces=ns)) != ('DashyNMS.Agent.Search', version):
        raise SystemExit('Nuspec identity/version mismatch')
    if repository is None or (repository.get('commit'), repository.get('url'), repository.get('type')) != (commit, 'https://github.com/DashyNMS/mcp', 'git'):
        raise SystemExit('Nuspec repository/commit mismatch')
