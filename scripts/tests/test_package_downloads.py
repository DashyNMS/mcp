import importlib.util
from pathlib import Path
import sys
import unittest
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location('package_downloads',
    Path(__file__).resolve().parents[1] / 'fetch-dependency-licences.py')
fetch = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fetch)


class PackageRedirectTests(unittest.TestCase):
    def test_every_redirect_must_use_https_on_a_reviewed_host(self):
        handler = fetch.PackageRedirectHandler()
        request = urllib.request.Request('https://nuget.pkg.github.com/example/package')
        request.add_unredirected_header('Authorization', 'synthetic-test-value')
        redirected = handler.redirect_request(request, None, 302, 'Found', {},
            'https://pkg-nuget.githubusercontent.com/example/package')
        self.assertIsNone(redirected.get_header('Authorization'))
        for url in ('http://pkg-nuget.githubusercontent.com/package',
                    'https://example.net/package', 'file:///tmp/package',
                    'https://raw.githubusercontent.com.example.net/package',
                    'https://user@raw.githubusercontent.com/package',
                    'https://raw.githubusercontent.com:8080/package'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                handler.redirect_request(request, None, 302, 'Found', {}, url)

    def test_storage_hosts_are_only_allowed_on_redirect(self):
        with self.assertRaises(ValueError):
            fetch.validate_download_url('https://pkg-nuget.githubusercontent.com/package')
        fetch.validate_download_url('https://raw.githubusercontent.com/example/project/revision/LICENSE')
