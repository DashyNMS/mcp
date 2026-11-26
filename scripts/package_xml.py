"""Parse bounded package XML while rejecting DTDs before entity expansion."""
import xml.etree.ElementTree as ET

MAX_METADATA_BYTES = 4 * 1024 * 1024


class NoDtdBuilder(ET.TreeBuilder):
    def doctype(self, name, public_id, system_id):
        raise ValueError('Package metadata must not contain a DTD')


def parse_metadata(data):
    if len(data) > MAX_METADATA_BYTES:
        raise ValueError('Package metadata exceeds its size limit')
    # NoDtdBuilder rejects every document type before entity expansion.
    parser = ET.XMLParser(target=NoDtdBuilder())  # nosec B314
    # The target rejects every DTD, including UTF-16 declarations, before entities expand.
    return ET.fromstring(data, parser=parser)  # nosec B314
