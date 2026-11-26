import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('package_xml', Path(__file__).resolve().parents[1] / 'package_xml.py')
xml = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xml)


class PackageMetadataTests(unittest.TestCase):
    def test_normal_utf8_and_utf16_metadata_retains_namespaces(self):
        text = '<package xmlns="urn:example"><metadata><id>Example</id></metadata></package>'
        for encoding in ['utf-8', 'utf-16']:
            self.assertEqual('Example', xml.parse_metadata(text.encode(encoding)).findtext('{urn:example}metadata/{urn:example}id'))

    def test_internal_and_external_dtds_are_rejected_before_expansion(self):
        texts = ['<!DOCTYPE package [<!ENTITY value "synthetic">]><package>&value;</package>',
                 '<!DOCTYPE package SYSTEM "file:///example"><package/>']
        for text in texts:
            for encoding in ['utf-8', 'utf-16']:
                with self.assertRaisesRegex(ValueError, 'DTD'):
                    xml.parse_metadata(text.encode(encoding))

    def test_metadata_has_a_size_limit(self):
        with self.assertRaisesRegex(ValueError, 'size limit'):
            xml.parse_metadata(b'x' * (xml.MAX_METADATA_BYTES + 1))
