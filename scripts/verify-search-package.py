"""CI-only pack inspection and clean package-consumer smoke build (no mobile workloads)."""
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

root = Path(__file__).resolve().parents[1]
version = os.environ['SEARCH_VERSION']
commit = os.environ['GITHUB_SHA']
if subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip() != commit:
    raise SystemExit('Checkout HEAD does not match expected source GITHUB_SHA')
if subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain', '--untracked-files=no'], text=True).strip():
    raise SystemExit('Tracked checkout changes prevent source provenance verification')
# Derive the expected CLR version from the requested package version.
match = re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?', version)
assert match, version
assembly_version = ', '.join((*match.groups(), '0'))
package = root / 'artifacts' / f'DashyNMS.Agent.Search.{version}.nupkg'
ns = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
with zipfile.ZipFile(package) as archive:
    metadata = ET.fromstring(archive.read('DashyNMS.Agent.Search.nuspec')).find('n:metadata', ns)
    assert metadata.findtext('n:id', namespaces=ns) == 'DashyNMS.Agent.Search'
    assert metadata.findtext('n:version', namespaces=ns) == version
    repository = metadata.find('n:repository', ns)
    assert repository.attrib['commit'] == commit
    assert repository.attrib['url'] == 'https://github.com/DashyNMS/mcp'
    # Search ships its own copy so packing never depends on files outside the
    # project; it must stay identical to the repository licence.
    assert archive.read('LICENSE') == (root / 'src/DashyNMS.Agent.Search/LICENSE').read_bytes() == (root / 'LICENSE').read_bytes()
    # Search bundles no dependency DLLs: consumers restore Core under its own
    # terms, so NOTICES.md says the project licence does not cover it.
    assert archive.read('NOTICES.md') == (root / 'NOTICES.md').read_bytes()
    assert archive.read('README.md') == (root / 'src/DashyNMS.Agent.Search/README.md').read_bytes()
    licence = metadata.find('n:license', ns)
    assert licence.attrib['type'] == 'expression' and licence.text == 'MIT'
    assert metadata.findtext('n:readme', namespaces=ns) == 'README.md'
    assert metadata.findtext('n:authors', namespaces=ns) == 'Thomas Pickup'
    assert archive.read('LICENSE').startswith(b'MIT License\n\nCopyright (c) 2026 Thomas Pickup\n')
    assert metadata.findtext('n:copyright', namespaces=ns) == 'Copyright (c) 2026 Thomas Pickup'
    # Fail closed for every ZIP file, including new NuGet asset directories.
    names = archive.namelist()
    assert len(names) == len(set(names)), 'Duplicate ZIP entries'
    fixed = {'DashyNMS.Agent.Search.nuspec', 'LICENSE', 'NOTICES.md', 'README.md',
             '[Content_Types].xml', '_rels/.rels', 'lib/net10.0/DashyNMS.Agent.Search.dll'}
    assert all(name in fixed or re.fullmatch(r'package/services/metadata/core-properties/[A-Za-z0-9]+\.psmdcp', name)
               for name in names), names
    groups = metadata.findall('n:dependencies/n:group', ns)
    assert len(groups) == 1 and groups[0].attrib['targetFramework'] == 'net10.0'
    dependency_root = metadata.find('n:dependencies', ns)
    assert list(dependency_root) == groups, 'Ungrouped dependencies'
    dependencies = list(groups[0])
    core = ET.parse(root / 'Directory.Build.props').findtext('.//DashyNMSCoreVersion')
    # NuGet may serialise a minimum dependency as a bare version or [version, ).
    assert len(dependencies) == 1
    dependency = dependencies[0]
    assert dependency.tag == '{' + ns['n'] + '}dependency'
    assert dependency.attrib['id'] == 'DashyNMS.Core'
    assert dependency.attrib['version'].replace(' ', '') in (core, f'[{core},)')
    assert 'lib/net10.0/DashyNMS.Agent.Search.dll' in archive.namelist()

with tempfile.TemporaryDirectory() as directory:
    smoke = Path(directory)
    config = ET.parse(root / 'nuget.config')
    config.find('packageSources').append(ET.Element('add', key='search-smoke', value=str(package.parent)))
    mapping = ET.SubElement(config.find('packageSourceMapping'), 'packageSource', key='search-smoke')
    ET.SubElement(mapping, 'package', pattern='DashyNMS.Agent.Search')
    config.write(smoke / 'nuget.config')
    (smoke / 'Smoke.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
<ItemGroup><PackageReference Include="DashyNMS.Agent.Search" Version="[{version}]" /></ItemGroup>
</Project>''')
    (smoke / 'Program.cs').write_text('''using System;
using System.Reflection;
using DashyNMS.Agent.Devices;
using DesktopNMS.Core.Api;
var search = typeof(HostDeviceSearch);
var core = typeof(ILibreNmsClient);
if (search.GetMethod("Create")!.GetParameters()[0].ParameterType != core)
    throw new Exception("Search factory does not use the restored Core type");
if (search.Assembly.GetName().Version != new Version(''' + assembly_version + '''))
    throw new Exception("Unexpected Search assembly version");
var informational = search.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
if (!informational.StartsWith("''' + version + '''+", StringComparison.Ordinal) || !informational.EndsWith("''' + commit + '''", StringComparison.Ordinal))
    throw new Exception("Search version/commit mismatch");
Console.WriteLine(core.Assembly.FullName);
''')
    env = dict(os.environ, NUGET_PACKAGES=str(smoke / 'packages'))
    subprocess.run(['dotnet', 'restore', str(smoke / 'Smoke.csproj'), '--configfile', str(smoke / 'nuget.config'), '--no-cache'], check=True, env=env)
    assets = json.loads((smoke / 'obj/project.assets.json').read_text())
    subprocess.run(['python3', str(root / 'scripts/collect-dependency-notices.py'),
                    str(smoke / 'obj/project.assets.json'), str(smoke / 'legal-evidence'),
                    str(root / 'docs/dependency-licences')], check=True)
    libraries = sorted(assets['libraries'])
    assert all(item['type'] == 'package' for item in assets['libraries'].values())
    assert not any(name.split('/')[0].lower().startswith('modelcontextprotocol') or name.split('/')[0].lower() == 'dashynms.agent' for name in libraries)
    assert f'DashyNMS.Agent.Search/{version}' in libraries
    assert f'DashyNMS.Core/{core}' in libraries
    # Traverse the entire resolved dependency closure, including transitive packages.
    for target in assets['targets'].values():
        resolved = {key.split('/')[0].lower() for key in target}
        assert set(target) == set(libraries), 'Target package identities differ from library inventory'
        for item in target.values():
            assert all(name.lower() in resolved for name in item.get('dependencies', {}))
        assert not any(name.startswith('modelcontextprotocol') or name in ('dashynms.agent', 'dashynms.mcp') for name in resolved), resolved
    # Compare the restored DLL with the producer DLL that solution tests used.
    with zipfile.ZipFile(package) as archive:
        assert archive.read('lib/net10.0/DashyNMS.Agent.Search.dll') == (root / 'src/DashyNMS.Agent.Search/bin/Release/net10.0/DashyNMS.Agent.Search.dll').read_bytes()
        assert archive.read('lib/net10.0/DashyNMS.Agent.Search.dll') == (smoke / 'packages/dashynms.agent.search' / version / 'lib/net10.0/DashyNMS.Agent.Search.dll').read_bytes()
    subprocess.run(['dotnet', 'build', str(smoke / 'Smoke.csproj'), '-c', 'Release', '--no-restore', '-warnaserror'], check=True, env=env)
    subprocess.run(['dotnet', str(smoke / 'bin/Release/net10.0/Smoke.dll')], check=True, env=env)

report = {'package': package.name, 'version': version, 'repositoryCommit': commit,
          'source': {key: os.environ['GITHUB_' + suffix] for key, suffix in {
              'event': 'EVENT_NAME', 'ref': 'REF', 'refProtected': 'REF_PROTECTED',
              'runId': 'RUN_ID', 'runAttempt': 'RUN_ATTEMPT', 'repository': 'REPOSITORY'}.items()},
          'sha256': hashlib.sha256(package.read_bytes()).hexdigest(), 'resolvedLibraries': libraries}
(root / 'artifacts/search-provenance.json').write_text(json.dumps(report, indent=2) + '\n')
