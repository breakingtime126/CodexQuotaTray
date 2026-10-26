#!/usr/bin/env python3
"""Mirror only the anonymous Codex Resets public status; never accept client data."""
import argparse
import datetime as dt
import json
import math
import os
from pathlib import Path
import re
import urllib.error
import urllib.request

SOURCE = 'https://codex-resets.com/api/v1/status'
REPOSITORY = 'breakingtime126/CodexQuotaTray'
BRANCH = 'reset-data'
LIMIT = 65536
UTC = dt.timezone.utc


def require(condition):
    if not condition:
        raise ValueError('Invalid public status schema')


def obj(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys.split()))


def text(value, maximum, empty=False):
    require(isinstance(value, str) and len(value) <= maximum and (empty or bool(value.strip())))
    require(not any(ord(c) < 32 and c not in '\n\r\t' for c in value))
    return value


def timestamp(value):
    text(value, 64)
    require('T' in value and re.search(r'(Z|[+-]\d\d:\d\d)$', value) is not None)
    parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(parsed.tzinfo is not None and 2000 <= parsed.year <= 2100)
    return parsed


def source(value):
    require(isinstance(value, dict))
    if value.get('type') == 'x_post':
        obj(value, 'type author url' if 'url' in value else 'type author')
        require(value['author'] == 'thsottiaux')
        if value.get('url') is not None:
            url = text(value['url'], 512)
            require(re.fullmatch(r'https://(?:www\.)?(?:x|twitter)\.com/thsottiaux/status/[0-9]{1,32}', url, re.I) is not None)
    elif value.get('type') == 'observed':
        obj(value, 'type url' if 'url' in value else 'type')
        if 'url' in value:
            url = text(value['url'], 512)
            require(re.match(r'^[A-Za-z][A-Za-z0-9+.-]*:', url) is not None)
    else:
        require(False)


def validate(api):
    obj(api, 'data meta')
    data, meta = api['data'], api['meta']
    obj(meta, 'api_version generated_at')
    require(meta['api_version'] == 'v1')
    timestamp(meta['generated_at'])
    obj(data, 'latest_reset scheduled_reset active_watch stats')
    for key in ('latest_reset', 'scheduled_reset'):
        item = data[key]
        if item is None:
            continue
        obj(item, 'id reset_type announced_at text source' + (' status scheduled_for' if key == 'scheduled_reset' else ''))
        text(item['id'], 64)
        require(item['reset_type'] in ('regular', 'banked'))
        timestamp(item['announced_at'])
        text(item['text'], 4096, empty=True)
        source(item['source'])
        if key == 'scheduled_reset':
            require(item['status'] == 'scheduled')
            if item['scheduled_for'] is not None:
                timestamp(item['scheduled_for'])
    watch = data['active_watch']
    if watch is not None:
        obj(watch, 'level reset_chance_percent forecast_window observed_at expires_at text source')
        require(watch['level'] in ('elevated', 'strong'))
        percent = watch['reset_chance_percent']
        require(percent is None or (type(percent) is int and 0 <= percent <= 100))
        text(watch['forecast_window'], 160)
        require(timestamp(watch['expires_at']) > timestamp(watch['observed_at']))
        text(watch['text'], 4096, empty=True)
        source(watch['source'])
    stats = data['stats']
    obj(stats, 'total last_reset_at days_since_last avg_interval_days')
    require(type(stats['total']) is int and 0 <= stats['total'] <= 2147483647)
    if stats['last_reset_at'] is not None:
        timestamp(stats['last_reset_at'])
    for name in ('days_since_last', 'avg_interval_days'):
        number = stats[name]
        require(number is None or (type(number) in (int, float) and math.isfinite(number) and number >= 0))
    return api


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def parse(body):
    require(0 < len(body) <= LIMIT)
    api = json.loads(body.decode('utf-8', errors='strict'), object_pairs_hook=unique,
                     parse_constant=lambda _: require(False))
    return validate(api)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError('Redirect rejected')


def fetch():
    request = urllib.request.Request(SOURCE, headers={'Accept': 'application/json', 'Cache-Control': 'no-cache',
                                                     'User-Agent': 'CodexQuotaTray-public-reset-mirror/1'})
    with urllib.request.build_opener(NoRedirect()).open(request, timeout=25) as response:
        require(response.status == 200 and response.headers.get_content_type() == 'application/json')
        return parse(response.read(LIMIT + 1))


def produce(fetcher=fetch, now=None):
    api = fetcher()  # Do not create a successful timestamp until a complete validated fetch.
    validate(api)
    now = now or dt.datetime.now(UTC)
    require(now.tzinfo is not None)
    envelope = dict(mirrorSchemaVersion=1, upstreamSuccessAtUtc=now.astimezone(UTC).isoformat().replace('+00:00', 'Z'),
                    source=SOURCE, data=api['data'], meta=api['meta'])
    body = (json.dumps(envelope, ensure_ascii=False, separators=(',', ':'), allow_nan=False) + '\n').encode('utf-8')
    require(len(body) <= LIMIT)
    return body


def write_snapshot(path, fetcher=fetch, now=None):
    body = produce(fetcher, now)
    temporary = path.with_suffix('.tmp')
    temporary.write_bytes(body)
    temporary.replace(path)
    return body


def publish(body):
    """Use the job-scoped built-in token; no persisted credential or additional secret."""
    token = os.environ['GH_TOKEN']
    require(os.environ.get('GITHUB_REPOSITORY') == REPOSITORY)
    def api(path, payload=None, method=None):
        request = urllib.request.Request('https://api.github.com/repos/' + REPOSITORY + path,
            data=None if payload is None else json.dumps(payload).encode(), method=method,
            headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                     'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json'})
        with urllib.request.build_opener(NoRedirect()).open(request, timeout=30) as response:
            return json.load(response)
    try:
        head = api('/git/ref/heads/' + BRANCH)['object']['sha']
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        head = None
    tree = api('/git/trees', {'tree': [{'path': 'status.json', 'mode': '100644', 'type': 'blob',
                                       'content': body.decode('utf-8')}]})
    commit = api('/git/commits', {'message': 'Refresh public reset status (Codex Resets attribution in source)',
        'tree': tree['sha'], 'parents': [] if head is None else [head]})
    if head is None:
        api('/git/refs', {'ref': 'refs/heads/' + BRANCH, 'sha': commit['sha']})
    else:
        api('/git/refs/heads/' + BRANCH, {'sha': commit['sha'], 'force': False}, 'PATCH')
    print('Published validated public reset snapshot to reset-data/status.json')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--publish', action='store_true')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    try:
        body = write_snapshot(args.output) if args.output else produce()
        if args.publish:
            publish(body)
        elif not args.output:
            print(body.decode(), end='')
    except Exception as error:
        # No response dumps, token/header logging, or failed-fetch timestamp update.
        raise SystemExit('Public reset mirror failed; previous snapshot unchanged (' + type(error).__name__ + ').')
