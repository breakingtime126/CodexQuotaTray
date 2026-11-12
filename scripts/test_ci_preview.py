"""Offline checks for the isolated prerelease publisher; no credentials/network."""
import hashlib
import importlib.util
import os
import pathlib
import tempfile
import unittest
from unittest.mock import patch

with patch.dict(os.environ, {'GH_TOKEN': 'offline-test-only'}):
    spec = importlib.util.spec_from_file_location('preview', pathlib.Path(__file__).with_name('ci-preview.py'))
    preview = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(preview)

class PreviewTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.cwd = os.getcwd()
        os.chdir(self.tmp.name)
        pathlib.Path('dist').mkdir()
        self.exe = b'MZ' + bytes(1024)
        pathlib.Path('dist/CodexQuotaTray.exe').write_bytes(self.exe)
        self.env = patch.dict(os.environ, {'SOURCE_SHA': 'a' * 40,
            'EXPECTED_EXE_SHA256': hashlib.sha256(self.exe).hexdigest()})
        self.env.start()
        self.calls = []
        self.assets = {}
        self.corrupt = False
        self.tag = None
        self.published = False
        self.tag_reads = 0
        self.move_tag = False
        self.fresh_prerelease = True
        self.all_releases = patch.object(preview.transport, 'all_releases', return_value=[])
        self.all_releases.start()
        self.api = patch.object(preview, 'request', side_effect=self.request)
        self.api.start()
    def tearDown(self):
        self.api.stop(); self.all_releases.stop(); self.env.stop()
        os.chdir(self.cwd); self.tmp.cleanup()
    def request(self, path, method='GET', data=None, raw=False, content_type=None):
        self.calls.append((path, method, data))
        if '/git/ref/' in path:
            self.tag_reads += 1
            if self.move_tag and self.tag_reads > 1:
                return {'object': {'type': 'commit', 'sha': 'b' * 40}}
            return self.tag or ({'object': {'type': 'commit', 'sha': 'a' * 40}} if self.published else None)
        if path.endswith('/releases') and method == 'POST':
            return {'id': 5}
        if 'uploads.github.com' in path:
            self.assets[path.rsplit('=', 1)[1]] = data
            return {}
        if '/releases/assets/' in path:
            return b'corrupt' if self.corrupt else self.assets[list(self.assets)[int(path.rsplit('/', 1)[1])]]
        if method == 'PATCH':
            self.published = True
            return {'target_commitish': 'a' * 40, 'draft': False, 'prerelease': True, 'tag_name': preview.TAG, 'html_url': 'https://github.com/example/preview'}
        return {'target_commitish': 'a' * 40, 'draft': True, 'prerelease': self.fresh_prerelease, 'tag_name': preview.TAG,
            'assets': [{'id': i, 'name': name} for i, name in enumerate(self.assets)]}
    def test_publishes_only_verified_prerelease(self):
        preview.main()
        self.assertFalse(preview.TAG.startswith('v'))
        self.assertEqual(set(self.assets), {'CodexQuotaTray.exe', 'SHA256SUMS'})
        writes = [d for p, m, d in self.calls if m in ('PATCH', 'POST') and isinstance(d, dict)]
        self.assertEqual(len(writes), 2)
        for data in writes:
            self.assertTrue(data['prerelease'])
            self.assertEqual(data['make_latest'], 'false')
            self.assertEqual(data['target_commitish'], 'a' * 40)
        self.assertFalse(any('update.json' in p for p, _, _ in self.calls))
    def test_changed_artifact_stops_before_network(self):
        pathlib.Path('dist/CodexQuotaTray.exe').write_bytes(b'MZ changed')
        with self.assertRaisesRegex(RuntimeError, 'tested EXE'): preview.main()
        self.assertEqual(self.calls, [])
    def test_existing_release_or_draft_is_never_overwritten(self):
        with patch.object(preview.transport, 'all_releases', return_value=[{'tag_name': preview.TAG}]):
            with self.assertRaisesRegex(RuntimeError, 'already exists'): preview.main()
        self.assertEqual(self.calls, [])
    def test_conflicting_tag_stops(self):
        self.tag = {'object': {'type': 'commit', 'sha': 'b' * 40}}
        with self.assertRaisesRegex(RuntimeError, 'elsewhere'): preview.main()
        self.assertFalse(any(m != 'GET' for _, m, _ in self.calls))
    def test_corrupt_uploaded_asset_is_not_published(self):
        self.corrupt = True
        with self.assertRaisesRegex(RuntimeError, 'bytes differ'): preview.main()
        self.assertFalse(any(m == 'PATCH' for _, m, _ in self.calls))
    def test_draft_not_prerelease_is_not_published(self):
        self.fresh_prerelease = False
        with self.assertRaisesRegex(RuntimeError, 'identity or assets'): preview.main()
        self.assertFalse(any(m == 'PATCH' for _, m, _ in self.calls))
    def test_tag_changed_during_upload_is_not_published(self):
        self.move_tag = True
        with self.assertRaisesRegex(RuntimeError, 'elsewhere'): preview.main()
        self.assertFalse(any(m == 'PATCH' for _, m, _ in self.calls))
    def test_invalid_sha_stops(self):
        with patch.dict(os.environ, {'SOURCE_SHA': 'main'}):
            with self.assertRaisesRegex(RuntimeError, 'source SHA'): preview.main()
        self.assertEqual(self.calls, [])

if __name__ == '__main__': unittest.main()
