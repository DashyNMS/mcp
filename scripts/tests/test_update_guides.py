"""Copying a guide into the bundle, with synthetic archives."""
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('guides', Path(__file__).resolve().parents[1] / 'update-guides.py')
guides = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guides)

COMMIT = 'a' * 40


class UpdateGuidesTests(unittest.TestCase):
    def archive(self, files):
        data = io.BytesIO()
        with tarfile.open(fileobj=data, mode='w:gz') as packed:
            for name, text in files.items():
                member = tarfile.TarInfo('example-abc/' + name)
                member.size = len(text.encode())
                packed.addfile(member, io.BytesIO(text.encode()))
        return data.getvalue()

    def test_only_the_guides_pages_are_taken(self):
        pages, version = guides.read(self.archive({
            'Directory.Build.props': '<Project><Version>2.0.0</Version></Project>',
            'docs/guide/README.md': '# Guide\r\n',
            'docs/guide/how-to/find.md': '# Find\n',
            'docs/guide/picture.png': 'x',
            'docs/guide/../../outside.md': '# Outside\n',
            'docs/DEVELOPMENT.md': '# Not for users\n',
        }))
        self.assertEqual({'docs/guide/README.md': '# Guide\n', 'docs/guide/how-to/find.md': '# Find\n'}, pages)
        self.assertEqual('2.0.0', version)

    def test_a_commit_without_a_guide_is_refused(self):
        with self.assertRaises(AssertionError):
            guides.read(self.archive({'Directory.Build.props': '<Version>2.0.0</Version>'}))

    def test_old_pages_go_and_the_other_product_stays(self):
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            guides.write('mobile', COMMIT, {'docs/guide/README.md': '# Mobile\n'}, '1.0.0', folder)
            guides.write('desktop', COMMIT, {'docs/guide/README.md': '# A\n', 'docs/guide/old.md': '# Old\n'}, '1.0.0', folder)
            guides.write('desktop', 'b' * 40, {'docs/guide/README.md': '# B\n'}, '2.0.0', folder)
            self.assertFalse((folder / 'desktop/docs/guide/old.md').exists())
            self.assertEqual('# Mobile\n', (folder / 'mobile/docs/guide/README.md').read_text())
            self.assertEqual({'desktop': {'version': '2.0.0', 'reference': 'b' * 40}, 'mobile': {'version': '1.0.0', 'reference': COMMIT}},
                             json.loads((folder / 'guides.json').read_text()))

    def test_a_branch_name_is_refused(self):
        with self.assertRaises(AssertionError):
            guides.main('desktop', 'main')


if __name__ == '__main__':
    unittest.main()
