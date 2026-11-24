"""Build one platform's standalone server: a single self-contained executable with its legal files.

Usage:
    publish-binary.py build <runtime> <version> <commit> <folder>
    publish-binary.py pack <runtime> <version> <folder> <archive directory>

`build` publishes into <folder>/dashynms-mcp-<version>-<runtime>. `pack` puts that
folder in one archive (.zip for Windows, .tar.gz otherwise) with its SHA-256 beside it.
They're apart so that macOS signing can come between them.

Unlike DashyNMS desktop's bundle, this one has the HTTP transport. Single-file is checked
rather than assumed: a native library left beside the executable would be one more file
to lose, and would mean the publish settings no longer do what this says they do.
"""
import hashlib
import json
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'src/DashyNMS.Mcp'
RUNTIMES = ('win-x64', 'win-arm64', 'osx-arm64', 'osx-x64', 'linux-x64', 'linux-arm64')


def executable(runtime):
    return 'dashynms-mcp.exe' if runtime.startswith('win-') else 'dashynms-mcp'


def name(runtime, version):
    return f'dashynms-mcp-{version}-{runtime}'


def shipped_assets(runtime):
    """The restore's record with only the packages that put files in the executable."""
    assets = json.loads((PROJECT / 'obj/project.assets.json').read_text(encoding='utf-8-sig'))
    target = next(key for key in assets['targets'] if key.endswith('/' + runtime))
    for library in list(assets['libraries']):
        entry = assets['targets'][target].get(library, {})
        files = [path for kind in ('runtime', 'native', 'resource', 'runtimeTargets') for path in entry.get(kind, {})]
        if not any(not path.endswith('/_._') for path in files):
            del assets['libraries'][library]
    return assets


def collect_legal(runtime, output):
    assets = shipped_assets(runtime)
    with tempfile.TemporaryDirectory() as directory:
        filtered = Path(directory) / 'project.assets.json'
        filtered.write_text(json.dumps(assets), encoding='utf-8')
        subprocess.run([sys.executable, '-X', 'utf8', str(ROOT / 'scripts/collect-dependency-notices.py'), str(filtered),
                        str(output / 'third-party'), str(ROOT / 'docs/dependency-licences')], check=True)

    # What "self-contained" adds: the .NET and ASP.NET Core runtimes, under their own notices.
    framework = next(iter(assets['project']['frameworks'].values()))
    packs = [f'{reference}.Runtime.{runtime}' for reference in framework['frameworkReferences']]
    for pack in packs:
        download = next((entry for entry in framework['downloadDependencies'] if entry['name'] == pack), None)
        if download is None:
            raise SystemExit(f"The restore doesn't record {pack}.")
        version = download['version'].strip('[]').split(',')[0].strip()
        folder = next((found for folder in assets['packageFolders']
                       if (found := Path(folder) / pack.lower() / version).is_dir()), None)
        if folder is None:
            raise SystemExit(f'{pack} {version} is not in the package cache.')
        target = output / 'third-party' / pack.lower()
        target.mkdir(parents=True)
        # The two packs spell LICENSE.TXT differently.
        present = {path.name.upper(): path for path in folder.iterdir()}
        for file in ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
            shutil.copyfile(present[file], target / file)


def build(runtime, version, commit, folder):
    output = Path(folder) / name(runtime, version)
    if output.exists():
        raise SystemExit(f'{output} exists.')
    with tempfile.TemporaryDirectory() as directory:
        published = Path(directory)
        # Uncompressed on macOS: a compressed single file on Apple silicon can crash
        # when it starts a child process (dotnet/runtime#123324), as desktop found.
        # Not trimmed, and invariant globalisation, for the reasons in the Dockerfile.
        subprocess.run(
            ['dotnet', 'publish', str(PROJECT), '-c', 'Release', '-r', runtime, '--self-contained', 'true', '-warnaserror',
             '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
             f'-p:EnableCompressionInSingleFile={"false" if runtime.startswith("osx-") else "true"}',
             '-p:InvariantGlobalization=true', '-p:DebugType=none', f'-p:Version={version}',
             f'-p:SourceRevisionId={commit}', '-p:ContinuousIntegrationBuild=true', '-o', str(published)], check=True)
        files = sorted(str(path.relative_to(published)) for path in published.rglob('*') if path.is_file())
        if files != [executable(runtime)]:
            raise SystemExit(f'Expected one file, got: {", ".join(files)}')
        output.mkdir(parents=True)
        shutil.copy2(published / executable(runtime), output / executable(runtime))
    for file in ('LICENSE', 'NOTICES.md'):
        shutil.copyfile(ROOT / file, output / file)
    (output / 'BUILD.txt').write_text(f'DashyNMS MCP {version} ({runtime}), {commit}\n', encoding='utf-8')
    collect_legal(runtime, output)
    print(f'{output}: {(output / executable(runtime)).stat().st_size / 1048576:.1f} MB')


def pack(runtime, version, folder, archives):
    source = Path(folder) / name(runtime, version)
    target = Path(archives)
    target.mkdir(parents=True, exist_ok=True)
    files = sorted(path for path in source.rglob('*') if path.is_file())
    if runtime.startswith('win-'):
        archive = target / f'{source.name}.zip'
        with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED) as out:
            for file in files:
                out.write(file, f'{source.name}/{file.relative_to(source).as_posix()}')
    else:
        archive = target / f'{source.name}.tar.gz'

        def plain(entry):
            entry.uid = entry.gid = 0
            entry.uname = entry.gname = ''
            runs = Path(entry.name).name == executable(runtime)
            entry.mode = stat.S_IRUSR | stat.S_IWUSR | stat.S_IRGRP | stat.S_IROTH | (0o111 if runs else 0)
            return entry

        with tarfile.open(archive, 'x:gz') as out:
            for file in files:
                out.add(file, f'{source.name}/{file.relative_to(source).as_posix()}', filter=plain)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    (target / f'{archive.name}.sha256').write_text(f'{digest}  {archive.name}\n', encoding='utf-8', newline='\n')
    print(f'{digest}  {archive.name}')


if __name__ == '__main__':
    command, runtime, *rest = sys.argv[1:]
    if runtime not in RUNTIMES:
        raise SystemExit(f'{runtime} is not one of {", ".join(RUNTIMES)}.')
    {'build': build, 'pack': pack}[command](runtime, *rest)
