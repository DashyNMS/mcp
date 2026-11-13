"""Run the Shared package's tests against the exact packed binary with a consumer-chosen Core.

Usage: verify-shared-core-compatibility.py <DashyNMS.Core version>

The Shared package is compiled against MCP's Core pin, but NuGet lets a host such as mobile
unify Core to a newer version. This restores the downloaded package (not a
project reference) beside an exact Core pin in a fresh package directory,
checks the resolved graph and DLL bytes, then runs the Shared-only tests on
that combination. It proves the tested host surface on plain .NET only; mobile
trimming/AOT and device runtime remain the consumer's own CI checks.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

core = sys.argv[1]
root = Path(__file__).resolve().parents[1]
version = os.environ['SHARED_VERSION']
expected_sha256 = os.environ['EXPECTED_SHARED_SHA256']
package = root / 'artifacts' / f'DashyNMS.Agent.Shared.{version}.nupkg'
# The expected hash comes from the verify job's output, not from the artefact,
# so a replaced package and report pair cannot pass together.
if hashlib.sha256(package.read_bytes()).hexdigest() != expected_sha256:
    raise SystemExit('Downloaded Shared package does not match the verified hash')
project = root / 'tests/DashyNMS.Agent.Shared.Tests/DashyNMS.Agent.Shared.Tests.csproj'
properties = [f'-p:SharedPackageVersion={version}', f'-p:DashyNMSCoreVersion={core}']

with tempfile.TemporaryDirectory() as directory:
    work = Path(directory)
    config = ET.parse(root / 'nuget.config')
    config.find('packageSources').append(ET.Element('add', key='shared-artefact', value=str(package.parent)))
    # The exact ID is more specific than DashyNMS.*, so only the Shared package comes from the artefact.
    mapping = ET.SubElement(config.find('packageSourceMapping'), 'packageSource', key='shared-artefact')
    ET.SubElement(mapping, 'package', pattern='DashyNMS.Agent.Shared')
    config.write(work / 'nuget.config')
    env = dict(os.environ, NUGET_PACKAGES=str(work / 'packages'))
    subprocess.run(['dotnet', 'restore', str(project), '--configfile', str(work / 'nuget.config'),
                    '--no-cache', *properties], check=True, env=env)

    assets = json.loads((project.parent / 'obj/project.assets.json').read_text())
    libraries = assets['libraries']
    if libraries.get(f'DashyNMS.Agent.Shared/{version}', {}).get('type') != 'package':
        raise SystemExit('The Shared package was not restored from the package')
    if f'DashyNMS.Core/{core}' not in libraries:
        raise SystemExit(f'Core {core} was not the resolved version')
    if any(name.split('/')[0] == 'DashyNMS.Core' and name != f'DashyNMS.Core/{core}' for name in libraries):
        raise SystemExit('More than one Core version resolved')
    banned = ('dashynms.agent', 'dashynms.mcp')
    if any(name.split('/')[0].lower() in banned or name.lower().startswith('modelcontextprotocol') for name in libraries):
        raise SystemExit('Agent or MCP assemblies entered the Shared-only test graph')

    restored = work / 'packages/dashynms.agent.shared' / version
    with zipfile.ZipFile(package) as archive:
        if archive.read('lib/net10.0/DashyNMS.Agent.Shared.dll') != (restored / 'lib/net10.0/DashyNMS.Agent.Shared.dll').read_bytes():
            raise SystemExit('Restored Shared DLL differs from the verified package')

    subprocess.run(['dotnet', 'test', str(project), '-c', 'Release', '--no-restore', *properties],
                   check=True, env=env)

summary = os.environ.get('GITHUB_STEP_SUMMARY')
if summary:
    with open(summary, 'a', encoding='utf-8') as out:
        out.write(f'DashyNMS.Agent.Shared `{version}` (sha256 `{expected_sha256}`) passed its tests with DashyNMS.Core `{core}`.\n')
