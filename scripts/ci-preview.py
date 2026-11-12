"""Publish only the isolated hash-removal preview; never create an updater manifest."""
import importlib.util
import os
import pathlib
import re

spec = importlib.util.spec_from_file_location('stable_transport', pathlib.Path(__file__).with_name('ci-publish.py'))
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
VERSION = '0.2.40-preview.1'
TAG = VERSION  # No v prefix: must not trigger the stable tag workflow.
REPO = transport.REPO
request = transport.request

def validate_tag(sha, allow_missing=True):
    ref = request(f'/repos/{REPO}/git/ref/tags/{TAG}')
    if ref is None and allow_missing:
        return
    if not ref or ref['object']['type'] != 'commit' or ref['object']['sha'] != sha:
        raise RuntimeError('Preview tag points elsewhere or is missing')

def main():
    sha = os.environ['SOURCE_SHA']
    if not re.fullmatch('[a-f0-9]{40}', sha):
        raise RuntimeError('Invalid source SHA')
    exe = pathlib.Path('dist/CodexQuotaTray.exe').read_bytes()
    digest = transport.digest(exe)
    if not 1 <= len(exe) <= 30 * 1024 * 1024 or not exe.startswith(b'MZ') or digest != os.environ['EXPECTED_EXE_SHA256']:
        raise RuntimeError('Preview differs from tested EXE')
    if any(r['tag_name'] == TAG for r in transport.all_releases()):
        raise RuntimeError('Preview release already exists; never overwrite it')
    validate_tag(sha)
    body = (
        f'预览版，仅供手动下载测试。构建来源：{sha}。\n\n'
        '移除 Desktop 额度查询额外的全文件 SHA-256 和字节一致性比较，保留官方签名、包身份、尺寸/PE 版本、路径及只读句柄检查。'
        'Windows 签名验证仍可能读取全文件；真实 CPU 降幅尚未验证。\n\n'
        'GitHub Windows CI 完成核心测试、net48 构建、原生菜单结构及离线传输测试。'
        '未测试真实账号额度读取和普通用户权限下的更新生命周期。\n\n'
        '此版本不设为 Latest，不发布 update.json，不向正式版自动更新用户推送。'
        'FileVersion 0.2.39.1 低于后续正式版 0.2.40.0。升级时请先退出旧程序。\n'
        'SHA256SUMS 仅供文件完整性对照，不是代码签名。'
    )
    release = request(f'/repos/{REPO}/releases', 'POST', {
        'tag_name': TAG, 'target_commitish': sha, 'name': f'CodexQuotaTray {VERSION}',
        'draft': True, 'prerelease': True, 'make_latest': 'false', 'body': body})
    rid = release['id']
    files = {'CodexQuotaTray.exe': exe, 'SHA256SUMS': f'{digest}  CodexQuotaTray.exe\n'.encode()}
    for name, data in files.items():
        request(f'https://uploads.github.com/repos/{REPO}/releases/{rid}/assets?name={name}',
                'POST', data, content_type='application/octet-stream')
    fresh = request(f'/repos/{REPO}/releases/{rid}')
    if not fresh['draft'] or not fresh['prerelease'] or fresh['tag_name'] != TAG or fresh['target_commitish'] != sha or len(fresh['assets']) != 2 or {a['name'] for a in fresh['assets']} != set(files):
        raise RuntimeError('Preview draft identity or assets changed')
    for asset in fresh['assets']:
        if request(f'/repos/{REPO}/releases/assets/{asset["id"]}', raw=True) != files[asset['name']]:
            raise RuntimeError('Preview asset bytes differ; draft left unpublished')
    validate_tag(sha)
    result = request(f'/repos/{REPO}/releases/{rid}', 'PATCH', {
        'draft': False, 'prerelease': True, 'make_latest': 'false', 'target_commitish': sha})
    if result['draft'] or not result['prerelease'] or result['tag_name'] != TAG or result['target_commitish'] != sha:
        raise RuntimeError('Preview publication identity mismatch')
    validate_tag(sha, allow_missing=False)
    print(f'Published prerelease {result["html_url"]}; EXE SHA256 {digest}')

if __name__ == '__main__':
    main()
