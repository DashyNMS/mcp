#!/usr/bin/env python3
"""Copy Desktop's or Mobile's user guide into the help tool's bundle (#164).

    update-guides.py <desktop|mobile> <commit>

Takes docs/guide from that commit of the product's public repository: pages only, as a
model reads no images. Writes them under src/DashyNMS.Agent.Shared/Help/Guides and records
the version and commit in guides.json, so an answer says what it describes. The server's
own guide is read from docs/guide directly. Run it again for a release, with the commit
the product is released from.
"""
import io
import json
from pathlib import Path
import re
import shutil
import sys
import tarfile
import urllib.request

REPOSITORIES = {'desktop': 'DashyNMS/desktop', 'mobile': 'DashyNMS/mobile'}
GUIDES = Path(__file__).resolve().parents[1] / 'src/DashyNMS.Agent.Shared/Help/Guides'
MAX_PAGE = 512 * 1024


def read(archive):
    """The guide's pages by repository path, and the version the repository is building."""
    pages, version = {}, None
    with tarfile.open(fileobj=io.BytesIO(archive), mode='r:gz') as packed:
        for member in packed:
            path = member.name.partition('/')[2]
            if not member.isfile() or '..' in path.split('/'):
                continue
            if path == 'Directory.Build.props':
                found = re.search(r'<Version>([0-9.]+)</Version>', packed.extractfile(member).read().decode('utf-8'))
                version = found and found.group(1)
            elif path.startswith('docs/guide/') and path.endswith('.md'):
                assert member.size <= MAX_PAGE, f'{path} is too large.'
                pages[path] = packed.extractfile(member).read().decode('utf-8').replace('\r\n', '\n')
    assert version, 'Directory.Build.props has no version.'
    assert 'docs/guide/README.md' in pages, 'There is no guide at that commit.'
    return pages, version


def write(product, commit, pages, version, guides):
    folder = guides / product
    shutil.rmtree(folder, ignore_errors=True)
    for path, text in pages.items():
        page = folder / path
        page.parent.mkdir(parents=True, exist_ok=True)
        page.write_text(text, encoding='utf-8', newline='\n')
    manifest = guides / 'guides.json'
    listed = json.loads(manifest.read_text(encoding='utf-8')) if manifest.exists() else {}
    listed[product] = {'version': version, 'reference': commit}
    manifest.write_text(json.dumps(dict(sorted(listed.items())), indent=2) + '\n', encoding='utf-8', newline='\n')


def main(product, commit):
    assert re.fullmatch(r'[0-9a-f]{40}', commit), 'Give the full commit, so the copy can be traced.'
    address = f'https://codeload.github.com/{REPOSITORIES[product]}/tar.gz/{commit}'
    with urllib.request.urlopen(address, timeout=120) as response:
        pages, version = read(response.read())
    write(product, commit, pages, version, GUIDES)
    print(f'{product} {version} at {commit}: {len(pages)} pages')


if __name__ == '__main__':
    main(*sys.argv[1:])
