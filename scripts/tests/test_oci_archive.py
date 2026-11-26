import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('oci_archive',
    Path(__file__).resolve().parents[1] / 'verify-oci-archive.py')
oci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oci)


class OciArchiveTests(unittest.TestCase):
    def archive(self, names):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        path = Path(folder.name) / 'image.tar'
        # Multiple aliases can all point at the same multi-platform index.
        descriptor = {'mediaType': 'application/vnd.oci.image.index.v1+json',
                      'digest': 'sha256:' + 'a' * 64, 'size': 123}
        index = {'schemaVersion': 2, 'manifests': [dict(descriptor,
            annotations={'org.opencontainers.image.ref.name': name}) for name in names]}
        data = json.dumps(index).encode()
        with tarfile.open(path, 'w') as archive:
            entry = tarfile.TarInfo('index.json')
            entry.size = len(data)
            archive.addfile(entry, io.BytesIO(data))
        return path

    def test_one_reference_to_a_multi_platform_index_is_accepted(self):
        oci.verify(self.archive(['2.0.0-nightly.1']))

    def test_channel_aliases_to_the_same_index_are_rejected(self):
        for names in [('2.0.0-edge.1', 'edge', 'sha-abc1234'),
                      ('2.0.0-nightly.1', 'nightly'), ('2.0.0', 'latest', '2.0')]:
            with self.subTest(names=names), self.assertRaisesRegex(ValueError, 'exactly one'):
                oci.verify(self.archive(names))

    def test_empty_archive_index_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            oci.verify(self.archive([]))
