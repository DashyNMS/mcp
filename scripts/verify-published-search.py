"""After publication, confirm GitHub Packages serves the exact verified bytes.

Downloads the published .nupkg through the feed's own service index (with a
short retry for propagation) and compares its SHA-256 with the verify job's
output. Then reads the package's GitHub metadata: mobile restores with its own
GITHUB_TOKEN, which can read this package only while it stays public, as Core is.
A failure here cannot unpublish the version; it flags it so no consumer pins it.
GitHub offers no API to change a package's visibility, so a first publication that
arrives private fails here until an organisation owner makes the package public.
"""
import base64
import hashlib
import json
import os
import time
import urllib.error
import urllib.request

version = os.environ['SEARCH_VERSION']
expected = os.environ['EXPECTED_SEARCH_SHA256']
token = os.environ['GITHUB_TOKEN']
feed = 'https://nuget.pkg.github.com/DashyNMS/index.json'
basic = 'Basic ' + base64.b64encode(f'DashyNMS:{token}'.encode()).decode()


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


_opener = urllib.request.build_opener(_NoRedirect)


def get(url, authorisation):
    """Fetch with the token, but follow a download redirect without it.

    Package downloads redirect to storage on another host; forwarding the
    token there would leak it and can make the storage reject the request.
    """
    request = urllib.request.Request(url, headers={'Authorization': authorisation, 'User-Agent': 'dashynms-search-publish'})
    try:
        with _opener.open(request, timeout=60) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.code not in (301, 302, 303, 307, 308):
            raise
        location = error.headers['Location']
    plain = urllib.request.Request(location, headers={'User-Agent': 'dashynms-search-publish'})
    with _opener.open(plain, timeout=120) as response:
        return response.read()


resources = json.loads(get(feed, basic))['resources']
base = next(r['@id'] for r in resources if r['@type'].startswith('PackageBaseAddress/3.0.0'))
# The token goes to this address, so never accept a cleartext one.
if not base.startswith('https://'):
    raise SystemExit(f'Package base address must use HTTPS: {base}')
lower = version.lower()
url = f"{base.rstrip('/')}/dashynms.agent.search/{lower}/dashynms.agent.search.{lower}.nupkg"
for attempt in range(10):
    try:
        published = get(url, basic)
        break
    except urllib.error.HTTPError as error:
        if error.code != 404 or attempt == 9:
            raise
        time.sleep(15)
actual = hashlib.sha256(published).hexdigest()
if actual != expected:
    raise SystemExit(f'Feed serves {actual}, not the verified {expected}')

try:
    package = json.loads(get('https://api.github.com/orgs/DashyNMS/packages/nuget/DashyNMS.Agent.Search',
                             f'Bearer {token}'))
except urllib.error.HTTPError as error:
    # Whether the Actions token may read package metadata is not documented
    # for NuGet; a refusal leaves access unverified rather than failing a
    # publication whose bytes were already confirmed above.
    if error.code not in (401, 403):
        raise
    package = None
if package is not None:
    repository = (package.get('repository') or {}).get('full_name')
    if (package.get('visibility'), repository) != ('public', 'DashyNMS/mcp'):
        raise SystemExit(f"Package is {package.get('visibility')} and linked to {repository}; "
                         'mobile needs a public package linked to DashyNMS/mcp')
access = ('public and linked to DashyNMS/mcp' if package is not None
          else 'visibility unverified (the Actions token could not read package metadata)')
if package is None:
    # A workflow annotation keeps this visible on the run page, not only in the summary.
    print('::warning title=Search package visibility unverified::Confirm DashyNMS.Agent.Search is public '
          'before mobile pins this version.')

summary = os.environ.get('GITHUB_STEP_SUMMARY')
if summary:
    with open(summary, 'a', encoding='utf-8') as out:
        out.write(f'Published DashyNMS.Agent.Search `{version}`; the feed serves sha256 `{actual}`; {access}.\n')
print(f'Published {version} sha256 {actual}; {access}')
