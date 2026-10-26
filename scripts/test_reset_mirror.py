import copy
import datetime as dt
import importlib.util
import json
import io
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('mirror', Path(__file__).with_name('reset-mirror.py'))
mirror = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mirror)
NOW = dt.datetime(2026, 10, 26, 9, 0, tzinfo=dt.timezone.utc)
FIXTURE = {'data': {'latest_reset': {'id': 'historic', 'reset_type': 'regular',
    'announced_at': '2020-01-01T00:00:00Z', 'text': 'Reset',
    'source': {'type': 'x_post', 'author': 'thsottiaux', 'url': 'https://x.com/thsottiaux/status/123'}},
    'scheduled_reset': None, 'active_watch': None,
    'stats': {'total': 1, 'last_reset_at': '2020-01-01T00:00:00Z', 'days_since_last': 100, 'avg_interval_days': 5}},
    'meta': {'api_version': 'v1', 'generated_at': '2026-10-26T09:00:00Z'}}


class MirrorTests(unittest.TestCase):
    def test_fresh_envelope_does_not_age_by_reset_event(self):
        result = json.loads(mirror.produce(lambda: copy.deepcopy(FIXTURE), NOW))
        self.assertEqual(result['upstreamSuccessAtUtc'], '2026-10-26T09:00:00Z')
        self.assertEqual(result['data']['latest_reset']['announced_at'], '2020-01-01T00:00:00Z')
        self.assertEqual(result['mirrorSchemaVersion'], 1)
        self.assertEqual(result['source'], mirror.SOURCE)

    def test_failure_preserves_prior_snapshot_and_timestamp(self):
        for failure in (TimeoutError(), ValueError(), OSError()):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'status.json'
                previous = mirror.write_snapshot(path, lambda: FIXTURE, NOW)
                def fail():
                    raise failure
                with self.assertRaises(type(failure)):
                    mirror.write_snapshot(path, fail, NOW + dt.timedelta(hours=1))
                self.assertEqual(path.read_bytes(), previous)
                self.assertFalse(path.with_suffix('.tmp').exists())

    def test_invalid_success_does_not_write(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'status.json'
            with self.assertRaises(ValueError):
                mirror.write_snapshot(path, lambda: {'data': None, 'meta': {}}, NOW)
            self.assertFalse(path.exists())

    def test_body_bounds_duplicates_and_json(self):
        for body in (b'', b' ' * 65537, b'{"data":{},"data":{},"meta":{}}', b'\xff', b'{"data":NaN}', b'[]'):
            with self.assertRaises((ValueError, UnicodeError)):
                mirror.parse(body)

    def test_schema_bounds(self):
        for mutate in (
            lambda x: x['meta'].update(api_version='v2'),
            lambda x: x['data']['stats'].update(total=True),
            lambda x: x['data']['stats'].update(avg_interval_days=float('inf')),
            lambda x: x['data']['latest_reset'].update(text='a' * 4097),
            lambda x: x['data']['latest_reset'].update(announced_at='2026-10-26T09:00:00'),
            lambda x: x['data']['latest_reset']['source'].update(url='https://evil.example/123'),
            lambda x: x['data'].update(extra='unexpected'),
        ):
            sample = copy.deepcopy(FIXTURE)
            mutate(sample)
            with self.assertRaises(ValueError):
                mirror.validate(sample)

    def test_scheduled_watch_and_null_links(self):
        sample = copy.deepcopy(FIXTURE)
        sample['data']['scheduled_reset'] = dict(sample['data']['latest_reset'], status='scheduled', scheduled_for='2026-10-26T10:00:00Z')
        sample['data']['latest_reset']['source']['url'] = None
        sample['data']['active_watch'] = {'level': 'strong', 'reset_chance_percent': 50,
            'forecast_window': 'today', 'observed_at': '2026-10-26T09:00:00Z',
            'expires_at': '2026-10-26T10:00:00Z', 'text': '', 'source': {'type': 'observed'}}
        mirror.validate(sample)
        sample['data']['active_watch']['reset_chance_percent'] = 101
        with self.assertRaises(ValueError):
            mirror.validate(sample)

    def test_redirects_rejected(self):
        with self.assertRaises(ValueError):
            mirror.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://evil.example')

    def test_throttle_never_publishes_or_creates_timestamp(self):
        with mock.patch.object(mirror, 'publish') as publish:
            def throttled():
                raise mirror.urllib.error.HTTPError(mirror.SOURCE, 429, 'Throttled', {}, None)
            with self.assertRaises(mirror.urllib.error.HTTPError):
                body = mirror.produce(throttled, NOW)
                mirror.publish(body)
            publish.assert_not_called()

    def test_publish_initializes_only_status_on_orphan_branch(self):
        calls = []
        class Opener:
            def open(self, request, timeout):
                calls.append(request)
                if len(calls) == 1:
                    raise mirror.urllib.error.HTTPError(request.full_url, 404, 'Not found', {}, None)
                return io.BytesIO(json.dumps({'sha': 'a' * 40}).encode())
        with mock.patch.dict(mirror.os.environ, {'GH_TOKEN': 'test-token', 'GITHUB_REPOSITORY': mirror.REPOSITORY}), \
             mock.patch.object(mirror.urllib.request, 'build_opener', return_value=Opener()):
            mirror.publish(mirror.produce(lambda: FIXTURE, NOW))
        tree = json.loads(calls[1].data)
        self.assertEqual([entry['path'] for entry in tree['tree']], ['status.json'])
        self.assertEqual(json.loads(calls[2].data)['parents'], [])
        self.assertEqual(json.loads(calls[3].data)['ref'], 'refs/heads/reset-data')

    def test_publish_existing_branch_is_non_force(self):
        calls = []
        class Opener:
            def open(self, request, timeout):
                calls.append(request)
                payload = {'object': {'sha': 'b' * 40}} if len(calls) == 1 else {'sha': 'a' * 40}
                return io.BytesIO(json.dumps(payload).encode())
        with mock.patch.dict(mirror.os.environ, {'GH_TOKEN': 'test-token', 'GITHUB_REPOSITORY': mirror.REPOSITORY}), \
             mock.patch.object(mirror.urllib.request, 'build_opener', return_value=Opener()):
            mirror.publish(mirror.produce(lambda: FIXTURE, NOW))
        self.assertEqual(json.loads(calls[2].data)['parents'], ['b' * 40])
        self.assertEqual(calls[3].method, 'PATCH')
        self.assertFalse(json.loads(calls[3].data)['force'])

    def test_success_replaces_once(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'status.json'
            mirror.write_snapshot(path, lambda: FIXTURE, NOW)
            mirror.write_snapshot(path, lambda: FIXTURE, NOW + dt.timedelta(minutes=10))
            self.assertEqual(json.loads(path.read_bytes())['upstreamSuccessAtUtc'], '2026-10-26T09:10:00Z')


if __name__ == '__main__':
    unittest.main()
