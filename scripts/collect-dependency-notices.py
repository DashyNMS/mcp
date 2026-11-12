"""Retain restored package legal files verbatim; reject missing licence evidence.

No licence text is inferred from an SPDX expression or a publisher's name.
Packages without embedded texts require a version-specific supplement fetched
from the exact source commit their nuspec records (scripts/fetch-dependency-licences.py).
"""
import base64
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile


def nuget_content_hash(data):
    """Return NuGet's package content hash, the value project.assets.json records.

    NuGet hashes the whole archive for unsigned packages, but for signed ones
    (every nuget.org package carries a repository signature) it hashes the archive
    as if the .signature.p7s entry had never been added, so the hash survives
    re-signing. Comparing the raw file hash therefore fails for signed packages.
    This mirrors NuGet's SignedPackageArchiveUtility.GetPackageContentHash for the
    layout NuGet signing produces (signature stored last, contiguous entries) and
    raises on anything else rather than guessing, so the check always fails closed.
    """
    eocd = data.rfind(b'PK\x05\x06')
    if eocd < 0:
        raise ValueError('no end of central directory record')
    _, disk, cd_disk, entries_on_disk, entries, cd_size, cd_offset, comment_length = struct.unpack_from('<IHHHHIIH', data, eocd)
    if disk or cd_disk or entries_on_disk != entries or eocd + 22 + comment_length != len(data) or cd_offset + cd_size != eocd or 0xFFFFFFFF in (cd_size, cd_offset):
        raise ValueError('unsupported archive layout (multi-disk, zip64 or trailing data)')
    records = []
    position = cd_offset
    for _ in range(entries):
        fields = struct.unpack_from('<IHHHHHHIIIHHHHHII', data, position)
        if fields[0] != 0x02014B50:
            raise ValueError('corrupt central directory')
        name_length, extra_length, comment = fields[10], fields[11], fields[12]
        size = 46 + name_length + extra_length + comment
        records.append({'position': position, 'size': size, 'compressed': fields[8], 'flags': fields[3],
                        'method': fields[4], 'offset': fields[16], 'name': data[position + 46:position + 46 + name_length]})
        position += size
    if position != eocd:
        raise ValueError('central directory size mismatch')
    signatures = [record for record in records if record['name'] == b'.signature.p7s']
    if not signatures:
        return base64.b64encode(hashlib.sha512(data).digest()).decode()
    signature = signatures[0]
    if len(signatures) != 1 or signature is not records[-1] or signature['offset'] != max(record['offset'] for record in records):
        raise ValueError('package signature must be the single, last archive entry')
    local = struct.unpack_from('<IHHHHHIIIHH', data, signature['offset'])
    if local[0] != 0x04034B50 or signature['flags'] & 0x08 or signature['method'] != 0:
        raise ValueError('package signature entry is not stored as NuGet signing writes it')
    signature_entry_size = 30 + local[9] + local[10] + signature['compressed']
    if signature['offset'] + signature_entry_size != cd_offset:
        raise ValueError('package signature entry does not end at the central directory')
    digest = hashlib.sha512()
    digest.update(data[:signature['offset']])
    digest.update(data[cd_offset:signature['position']])
    digest.update(data[eocd:eocd + 8])
    digest.update(struct.pack('<HHII', entries - 1, entries - 1, cd_size - signature['size'], cd_offset - signature_entry_size))
    digest.update(data[eocd + 20:])
    return base64.b64encode(digest.digest()).decode()


assets = json.loads(Path(sys.argv[1]).read_text())
output = Path(sys.argv[2])
supplements = Path(sys.argv[3])
if output.exists() and any(output.iterdir()):
    raise SystemExit('Legal evidence output must be empty to prevent stale evidence reuse')
output.mkdir(parents=True, exist_ok=True)
records = []
missing = []
for identity, library in sorted(assets['libraries'].items()):
    if library['type'] != 'package':
        continue
    package_path = library['path']
    roots = [Path(folder) / package_path for folder in assets['packageFolders']]
    folder = next((folder for folder in roots if folder.is_dir()), None)
    if folder is None:
        raise SystemExit(f'{identity}: restored package directory is missing')
    archives = list(folder.glob('*.nupkg'))
    if len(archives) != 1:
        raise ValueError(f'{identity}: expected one restored package archive')
    archive_path = archives[0]
    archive_bytes = archive_path.read_bytes()
    # The assets file records NuGet's content hash, not a raw file hash; see
    # nuget_content_hash. NuGet's own metadata must agree, and the raw bytes must
    # still match the full-file hash NuGet wrote when it extracted the package.
    content_hash = nuget_content_hash(archive_bytes)
    if content_hash != library['sha512']:
        raise ValueError(f'{identity}: restored archive does not match assets content hash')
    metadata_file = folder / '.nupkg.metadata'
    if metadata_file.is_file() and json.loads(metadata_file.read_text()).get('contentHash') != content_hash:
        raise ValueError(f'{identity}: NuGet metadata content hash disagrees with the archive')
    raw_hash_files = list(folder.glob('*.nupkg.sha512'))
    if len(raw_hash_files) == 1 and raw_hash_files[0].read_text().strip() != base64.b64encode(hashlib.sha512(archive_bytes).digest()).decode():
        raise ValueError(f'{identity}: restored archive changed after NuGet extracted it')
    destination = output / package_path
    destination.mkdir(parents=True, exist_ok=True)
    files = {}
    with zipfile.ZipFile(archive_path) as archive:
        if len(archive.namelist()) != len(set(archive.namelist())):
            raise SystemExit(f'{identity}: duplicate archive entries')
        nuspecs = [name for name in archive.namelist() if name.endswith('.nuspec')]
        if len(nuspecs) != 1:
            raise ValueError(f'{identity}: ambiguous nuspec')
        nuspec = archive.read(nuspecs[0])
        metadata = ET.fromstring(nuspec)
        # Strip the optional NuGet XML namespace for consistent metadata access.
        for element in metadata.iter():
            element.tag = element.tag.rsplit('}', 1)[-1]
        licence = metadata.find('./metadata/license')
        declared_file = (licence.text or '').strip().replace('\\', '/') if licence is not None and licence.get('type') == 'file' else None
        if declared_file is not None and (not declared_file or declared_file not in archive.namelist()):
            raise SystemExit(f'{identity}: declared licence file {declared_file!r} is missing')
        expression = (licence.text or '').strip() if licence is not None and licence.get('type') == 'expression' else None
        licence_files = set()
        for name in archive.namelist():
            if name.endswith('/'):
                continue
            path = PurePosixPath(name)
            if path.is_absolute() or '..' in path.parts:
                raise ValueError(f'{identity}: unsafe archive path')
            is_licence = bool(re.fullmatch(r'(?i)(?:licen[cs]e|copying)(?:[._-][^.]+)?', path.name))
            is_legal_text = bool(re.fullmatch(r'(?i)(?:licen[cs]e|copying|copyright|notice|notices|third[._-]party[._-]notices)(?:[._-][^.]+)?', path.name))
            # An anchored name and text extension prevent collecting DLLs such as
            # Microsoft.Extensions.Licensing.dll or NOTICE.png.
            is_legal_text = is_legal_text and path.suffix.lower() in ('', '.txt', '.md', '.rst', '.html', '.htm')
            if name == declared_file or is_legal_text:
                data = archive.read(name)
                try:
                    text = data.decode('utf-8-sig')
                except UnicodeDecodeError:
                    raise SystemExit(f'{identity}: legal file {name} is not UTF-8 text; review a supplement')
                if any(ord(char) < 32 and char not in '\t\r\n' for char in text):
                    raise SystemExit(f'{identity}: legal file {name} contains binary data')
                if not data.strip():
                    raise ValueError(f'{identity}: empty legal file {name}')
                target = destination / 'package' / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                files['package/' + name] = hashlib.sha256(data).hexdigest()
                if name == declared_file or (is_licence and is_legal_text):
                    licence_files.add(target)
        has_text = bool(licence_files)
        supplement = supplements / package_path
        if (supplement / 'provenance.json').is_file():
            evidence = json.loads((supplement / 'provenance.json').read_text())
            if evidence['packageSha256'] != hashlib.sha256(archive_path.read_bytes()).hexdigest():
                raise ValueError(f'{identity}: supplement package hash mismatch')
            # Bind the texts to the source that built these bytes: the nuspec's own
            # repository commit, not a branch or a revision chosen by hand.
            repository = metadata.find('./metadata/repository')
            repository_url = (repository.get('url') or '').removesuffix('.git') if repository is not None else ''
            commit = repository.get('commit', '') if repository is not None else ''
            if not repository_url.startswith('https://github.com/') or not re.fullmatch(r'[0-9a-f]{40}', commit) or evidence.get('upstreamRevision') != commit or evidence.get('upstreamRepository') != repository_url:
                raise SystemExit(f'{identity}: supplement must come from the nuspec repository commit')
            prefix = f"https://raw.githubusercontent.com/{repository_url.removeprefix('https://github.com/')}/{commit}/"
            supplement_licence = False
            for name, source in evidence['files'].items():
                path = PurePosixPath(name)
                if not name or path.is_absolute() or '..' in path.parts or '/' in name or '\\' in name or not source['url'].startswith(prefix) or source.get('role') not in ('licence', 'notice'):
                    raise ValueError(f'{identity}: invalid supplement provenance')
                if not (supplement / name).is_file():
                    raise SystemExit(f'{identity}: missing supplement text {name}')
                data = (supplement / name).read_bytes()
                if not data.strip() or hashlib.sha256(data).hexdigest() != source['sha256']:
                    raise ValueError(f'{identity}: supplement text hash mismatch')
                try:
                    legal_text = data.decode('utf-8-sig')
                except UnicodeDecodeError:
                    raise SystemExit(f'{identity}: supplement {name} is not UTF-8 text')
                if any(ord(char) < 32 and char not in '\t\r\n' for char in legal_text):
                    raise SystemExit(f'{identity}: supplement {name} contains binary data')
                supplement_licence |= source['role'] == 'licence'
                target = destination / 'supplement' / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
                files['supplement/' + name] = source
                if source['role'] == 'licence':
                    licence_files.add(target)
            (destination / 'supplement-provenance.json').write_bytes((supplement / 'provenance.json').read_bytes())
            has_text |= supplement_licence
        if not has_text:
            missing.append(f'{identity}: no actual licence text (a notice or SPDX expression alone is insufficient)')
        if expression == 'MIT' and has_text:
            texts = '\n'.join(path.read_text(encoding='utf-8-sig') for path in licence_files)
            if not all(part in texts.lower() for part in ('permission is hereby granted', 'copyright', 'the above copyright notice', 'the software is provided')):
                missing.append(f'{identity}: MIT expression lacks actual MIT terms and copyright evidence')
        (destination / 'package.nuspec').write_bytes(nuspec)
    records.append({'package': identity, 'packageSha256': hashlib.sha256(archive_path.read_bytes()).hexdigest(),
                    'restoreSha512': library.get('sha512'), 'licenceExpression': expression, 'files': files})
(output / 'inventory.json').write_text(json.dumps(records, indent=2) + '\n')
if missing:
    raise SystemExit('Missing verified licence texts (review exact package/upstream provenance): ' + ', '.join(missing))
