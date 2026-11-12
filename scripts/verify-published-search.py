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
import urllib.parse
import urllib.request

version = os.environ['SEARCH_VERSION']
expected = os.environ['EXPECTED_SEARCH_SHA256']
token = os.environ['GITHUB_TOKEN']
feed = 'https://nuget.pkg.github.com/DashyNMS/index.json'
basic = 'Basic ' + base64.b64encode(f'DashyNMS:{token}'.encode()).decode()
# The token may only travel to these hosts, and only over HTTPS. The feed's
# service index chooses the download URL, so a misconfigured or tampered index
# must not be able to point the credential anywhere else.
CREDENTIAL_HOSTS = {'nuget.pkg.github.com', 'api.github.com'}
PACKAGE_PAGE = 'https://github.com/orgs/DashyNMS/packages/nuget/package/DashyNMS.Agent.Search'
LINKED_PAGE = 'https://github.com/DashyNMS/mcp/pkgs/nuget/DashyNMS.Agent.Search'


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


_opener = urllib.request.build_opener(_NoRedirect)


def require_https(url, hosts=None):
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != 'https' or (hosts is not None and parts.hostname not in hosts):
        raise SystemExit(f'Refusing to send the token to {url}: only HTTPS to {sorted(CREDENTIAL_HOSTS)} is allowed')


def get(url, authorisation):
    """Fetch with the token, but follow a download redirect without it.

    Package downloads redirect to storage on another host; forwarding the
    token there would leak it and can make the storage reject the request.
    """
    require_https(url, CREDENTIAL_HOSTS)
    request = urllib.request.Request(url, headers={'Authorization': authorisation, 'User-Agent': 'dashynms-search-publish'})
    try:
        with _opener.open(request, timeout=60) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.code not in (301, 302, 303, 307, 308):
            raise
        location = urllib.parse.urljoin(url, error.headers['Location'])
    # No token here, but still HTTPS: the hash check guards the bytes, and a
    # plain-HTTP hop would let anyone on the path see or swap them first.
    if urllib.parse.urlsplit(location).scheme != 'https':
        raise SystemExit(f'Download redirected to non-HTTPS {location}')
    plain = urllib.request.Request(location, headers={'User-Agent': 'dashynms-search-publish'})
    with _opener.open(plain, timeout=120) as response:
        return response.read()


resources = json.loads(get(feed, basic))['resources']
base = next(r['@id'] for r in resources if r['@type'].startswith('PackageBaseAddress/3.0.0'))
require_https(base, CREDENTIAL_HOSTS)
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



def anonymous_status(url):
    """Status and redirect target of an unauthenticated, unfollowed request."""
    request = urllib.request.Request(url, headers={'User-Agent': 'dashynms-search-publish'})
    try:
        with _opener.open(request, timeout=60) as response:
            return response.status, None
    except urllib.error.HTTPError as error:
        location = error.headers.get('Location')
        return error.code, urllib.parse.urljoin(url, location) if location else None


def anonymous_access():
    """Show public visibility and linkage without any credential.

    A private package's page is a 404 to an anonymous visitor. A public package
    linked to a repository redirects to that repository's package page, as
    DashyNMS.Core's does to DashyNMS/core. This relies on GitHub's web routes,
    not a documented API, so any other answer fails rather than passes.
    """
    status, location = anonymous_status(PACKAGE_PAGE)
    if status in (301, 302, 303, 307, 308) and location == LINKED_PAGE:
        status, _ = anonymous_status(LINKED_PAGE)
        if status == 200:
            return None
        return f'its linked page {LINKED_PAGE} answered {status} anonymously'
    if status == 404:
        return 'anonymous visitors cannot see it, so it is not public'
    return f'its page answered {status} with redirect {location}, not {LINKED_PAGE}'


# GitHub documents the packages REST API for classic personal access tokens
# only, so the Actions token may be refused. A refusal falls through to the
# anonymous check; it never counts as success on its own.
try:
    package = json.loads(get('https://api.github.com/orgs/DashyNMS/packages/nuget/DashyNMS.Agent.Search',
                             f'Bearer {token}'))
except urllib.error.HTTPError as error:
    if error.code not in (401, 403):
        raise
    package = None
    print(f'::notice::Package metadata refused with {error.code}; checking access anonymously instead')
if package is not None:
    repository = (package.get('repository') or {}).get('full_name')
    if (package.get('visibility'), repository) != ('public', 'DashyNMS/mcp'):
        raise SystemExit(f"Package is {package.get('visibility')} and linked to {repository}; "
                         'mobile needs a public package linked to DashyNMS/mcp. GitHub has no API to '
                         'change visibility: an organisation owner must set it public in the package '
                         'settings, then re-run all jobs.')
    access = 'public and linked to DashyNMS/mcp (package metadata)'
else:
    # Brief retries cover page caching just after the first publication.
    for attempt in range(3):
        problem = anonymous_access()
        if problem is None or attempt == 2:
            break
        time.sleep(20)
    if problem is not None:
        raise SystemExit(f'Cannot confirm DashyNMS.Agent.Search is public and linked to DashyNMS/mcp: {problem}. '
                         'GitHub has no API to change visibility: an organisation owner must set it public '
                         'in the package settings, then re-run all jobs.')
    access = 'public and linked to DashyNMS/mcp (anonymous package page)'

summary = os.environ.get('GITHUB_STEP_SUMMARY')
if summary:
    with open(summary, 'a', encoding='utf-8') as out:
        out.write(f'Published DashyNMS.Agent.Search `{version}`; the feed serves sha256 `{actual}`; {access}.\n')
print(f'Published {version} sha256 {actual}; {access}')
