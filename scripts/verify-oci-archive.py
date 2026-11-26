"""Reject an OCI export with ambiguous references before Skopeo copies it.

Multiple tags can create multiple entries for the same multi-platform index.
Skopeo's default archive selection requires exactly one entry, even when their
image digests agree. Platforms and attestations belong inside that nested index.
"""
import json
from pathlib import Path
import sys
import tarfile


def verify(path):
    with tarfile.open(path) as archive:
        with archive.extractfile('index.json') as stream:
            index = json.load(stream)
    if len(index['manifests']) != 1:
        raise ValueError('OCI archive must contain exactly one image reference; '
                         'apply channel aliases during publication')


if __name__ == '__main__':
    verify(Path(sys.argv[1]))
