"""Verify artefact identity; --for-publication additionally requires protected main.

The unsigned report is not an attestation. Expected source context must come from
an independently trusted Actions run, never from the downloaded report itself.
This script checks identity and integrity; it does not authorise publication.
"""
import argparse
import hashlib
import json
import os
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--for-publication', action='store_true')
args = parser.parse_args()
artifacts = Path(__file__).resolve().parents[1] / 'artifacts'
report = json.loads((artifacts / 'shared-provenance.json').read_text())
version = os.environ['SHARED_VERSION']
commit = os.environ['GITHUB_SHA']
if subprocess.check_output(['git', '-C', str(artifacts.parent), 'rev-parse', 'HEAD'], text=True).strip() != commit:
    raise SystemExit('Checkout HEAD does not match expected source GITHUB_SHA')
if subprocess.check_output(['git', '-C', str(artifacts.parent), 'status', '--porcelain', '--untracked-files=no'], text=True).strip():
    raise SystemExit('Tracked checkout changes prevent source provenance verification')
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
if version != f"2.0.0-ci.{source['runId']}.{source['runAttempt']}":
    raise SystemExit('Version does not match source run/attempt')
expected = f'DashyNMS.Agent.Shared.{version}.nupkg'
if (report['package'], report['version'], report['repositoryCommit']) != (expected, version, commit):
    raise SystemExit('Provenance identity does not match the expected workflow run')
packages = list(artifacts.glob('*.nupkg'))
if {p.name for p in artifacts.iterdir()} != {expected, 'shared-provenance.json'} or len(packages) != 1 or packages[0].name != expected:
    raise SystemExit('Expected exactly the verified Shared package')
if hashlib.sha256(packages[0].read_bytes()).hexdigest() != report['sha256']:
    raise SystemExit('Package SHA-256 does not match provenance')
with zipfile.ZipFile(packages[0]) as archive:
    ns = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
    if archive.namelist().count('DashyNMS.Agent.Shared.nuspec') != 1:
        raise SystemExit('Expected one Shared nuspec')
    metadata = ET.fromstring(archive.read('DashyNMS.Agent.Shared.nuspec')).find('n:metadata', ns)
    repository = metadata.find('n:repository', ns)
    if (metadata.findtext('n:id', namespaces=ns), metadata.findtext('n:version', namespaces=ns)) != ('DashyNMS.Agent.Shared', version):
        raise SystemExit('Nuspec identity/version mismatch')
    if repository is None or (repository.get('commit'), repository.get('url'), repository.get('type')) != (commit, 'https://github.com/DashyNMS/mcp', 'git'):
        raise SystemExit('Nuspec repository/commit mismatch')
    if archive.read('NOTICES.md') != (artifacts.parent / 'NOTICES.md').read_bytes():
        raise SystemExit('Shared notices content mismatch')
    licence = metadata.find('n:license', ns)
    if licence is None or licence.get('type') != 'expression' or licence.text != 'MIT':
        raise SystemExit('Expected MIT licence expression with bundled actual text')
    if archive.read('LICENSE') != (artifacts.parent / 'src/DashyNMS.Agent.Shared/LICENSE').read_bytes():
        raise SystemExit('Shared MIT licence content mismatch')
    if metadata.findtext('n:authors', namespaces=ns) != 'Thomas Pickup' or metadata.findtext('n:copyright', namespaces=ns) != 'Copyright (c) 2026 Thomas Pickup':
        raise SystemExit('Shared attribution mismatch')
