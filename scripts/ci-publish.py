"""Publish an exact paired EXE+signed manifest; never overwrite a published release."""
import base64, hashlib, json, os, pathlib, re, urllib.error, urllib.parse, urllib.request
REPO = 'breakingtime126/CodexQuotaTray'
API = 'https://api.github.com'
TOKEN = os.environ['GH_TOKEN']
class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        url = urllib.parse.urlparse(newurl)
        if url.scheme != 'https' or url.hostname not in {'api.github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com'}:
            raise RuntimeError('Unexpected asset redirect')
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None and url.hostname != urllib.parse.urlparse(req.full_url).hostname:
            new.remove_header('Authorization')
        return new
OPENER = urllib.request.build_opener(SafeRedirect())
def request(path, method='GET', data=None, raw=False, content_type=None):
    url = path if path.startswith('https://') else API + path
    if urllib.parse.urlparse(url).hostname not in {'api.github.com', 'uploads.github.com'}:
        raise RuntimeError('Unexpected API host')
    headers = {'Authorization': 'Bearer ' + TOKEN, 'Accept': 'application/octet-stream' if raw else 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'CodexQuotaTray-release'}
    if isinstance(data, dict):
        data = json.dumps(data).encode(); content_type = 'application/json'
    if content_type: headers['Content-Type'] = content_type
    try:
        with OPENER.open(urllib.request.Request(url, data=data, headers=headers, method=method), timeout=120) as resp:
            result = resp.read()
    except urllib.error.HTTPError as e:
        if e.code == 404 and method == 'GET': return None
        raise RuntimeError(f'GitHub API {method} failed with HTTP {e.code}') from None
    return result if raw else (json.loads(result) if result else None)
def all_releases():
    result = []
    for page in range(1, 101):
        items = request(f'/repos/{REPO}/releases?per_page=100&page={page}')
        if not isinstance(items, list): raise RuntimeError('Cannot list releases')
        result.extend(items)
        if len(items) < 100: return result
    raise RuntimeError('Release pagination limit reached')
def digest(data): return hashlib.sha256(data).hexdigest()
def main():
    version, sha = os.environ['RELEASE_VERSION'], os.environ['SOURCE_SHA']
    if not re.fullmatch(r'(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){3}', version) or not re.fullmatch('[a-f0-9]{40}', sha):
        raise RuntimeError('Invalid release identity')
    tag = 'v' + version
    files = {n: (pathlib.Path('dist') / n).read_bytes() for n in ('CodexQuotaTray.exe', 'update.json')}
    payload = json.loads(base64.b64decode(json.loads(files['update.json'])['payload'], validate=True))
    if (payload['version'], payload['size'], payload['sha256'], payload['assetUrl']) != (version, len(files['CodexQuotaTray.exe']), digest(files['CodexQuotaTray.exe']), f'https://github.com/{REPO}/releases/download/{tag}/CodexQuotaTray.exe'):
        raise RuntimeError('Manifest does not match release EXE')
    ref = request(f'/repos/{REPO}/git/ref/tags/{tag}')
    if ref:
        obj = ref['object']
        for _ in range(5):
            if obj['type'] == 'commit': break
            if obj['type'] != 'tag': raise RuntimeError('Unexpected tag object')
            obj = request(f'/repos/{REPO}/git/tags/{obj["sha"]}')['object']
        if obj['type'] != 'commit' or obj['sha'] != sha: raise RuntimeError('Existing tag differs from tested source')
    releases = all_releases()
    current = [r for r in releases if r['tag_name'] == tag]
    if len(current) > 1: raise RuntimeError('Ambiguous draft release')
    release = current[0] if current else None
    if release and not release['draft']: raise RuntimeError('Already published: refusing to modify any asset')
    for old in releases:
        if old['draft'] or old['prerelease']: continue
        if re.fullmatch(r'v[0-9]+(?:\.[0-9]+){3}', old['tag_name']) and tuple(map(int, old['tag_name'][1:].split('.'))) >= tuple(map(int, version.split('.'))):
            raise RuntimeError('Refusing duplicate or older stable release')
    if release:
        target = release['target_commitish']
        resolved = request(f'/repos/{REPO}/commits/{urllib.parse.quote(target, safe="")}')
        if not resolved or resolved['sha'] != sha: raise RuntimeError('Draft target differs from tested source')
        if release['prerelease']: raise RuntimeError('Expected stable draft, not prerelease')
    else:
        release = request(f'/repos/{REPO}/releases', 'POST', {'tag_name': tag, 'target_commitish': sha, 'name': f'CodexQuotaTray {version}', 'draft': True, 'prerelease': False, 'body': f'Built and core-tested by GitHub Actions from {sha}.\nWindows unelevated updater lifecycle smoke is not executed on the administrator hosted runner.'})
    rid = release['id']
    assets = release['assets']
    if any(a['name'] not in files for a in assets): raise RuntimeError('Draft contains unexpected assets; refusing changes')
    if len({a['name'] for a in assets}) != len(assets): raise RuntimeError('Duplicate draft assets')
    # Validate all existing assets before any deletion. Only old JSON may be replaced.
    upload = set(files)
    delete = []
    for asset in assets:
        content = request(f'/repos/{REPO}/releases/assets/{asset["id"]}', raw=True)
        if content == files[asset['name']]: upload.remove(asset['name'])
        elif (asset['name'] == 'update.json' and version == '0.2.34.0'
              and os.environ.get('REPLACE_DRAFT_MANIFEST') == 'true'
              and content is not None
              and digest(content) == '2a19951a343b75cdc3752d4617a45886bc46d73c4fba0fd9e88c67ded1223022'):
            delete.append(asset['id'])
        else: raise RuntimeError('Existing draft asset differs; replacement not permitted')
    fresh = request(f'/repos/{REPO}/releases/{rid}')
    if not fresh['draft']: raise RuntimeError('Release became public; refusing changes')
    for aid in delete: request(f'/repos/{REPO}/releases/assets/{aid}', 'DELETE')
    for name in sorted(upload):
        url = f'https://uploads.github.com/repos/{REPO}/releases/{rid}/assets?name={urllib.parse.quote(name)}'
        request(url, 'POST', files[name], content_type='application/octet-stream')
    fresh = request(f'/repos/{REPO}/releases/{rid}')
    if not fresh['draft'] or {a['name'] for a in fresh['assets']} != set(files) or len(fresh['assets']) != 2:
        raise RuntimeError('Draft state/asset set changed')
    for asset in fresh['assets']:
        if request(f'/repos/{REPO}/releases/assets/{asset["id"]}', raw=True) != files[asset['name']]:
            raise RuntimeError('Uploaded asset bytes failed verification; draft left unpublished')
    result = request(f'/repos/{REPO}/releases/{rid}', 'PATCH', {'draft': False, 'make_latest': 'true', 'target_commitish': sha})
    if result['draft']: raise RuntimeError('Publication did not complete')
    print(f'Published {result["html_url"]}; EXE SHA256 {digest(files["CodexQuotaTray.exe"])}')
if __name__ == '__main__': main()
