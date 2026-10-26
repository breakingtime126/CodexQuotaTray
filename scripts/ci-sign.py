"""CI-only wrapper. Never logs, persists, or publishes the private signing key."""
import base64, hashlib, json, os, pathlib, re, subprocess, tempfile
ROOT = pathlib.Path(__file__).resolve().parents[1]
def run(*args, **kwargs):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kwargs).stdout

def main():
    exe = ROOT / 'dist/CodexQuotaTray.exe'
    if hashlib.sha256(exe.read_bytes()).hexdigest() != os.environ['EXPECTED_EXE_SHA256']:
        raise RuntimeError('Artifact differs from tested build')
    key_text = os.environ.pop('UPDATE_SIGNING_KEY_PEM', '')
    if not key_text.strip():
        raise RuntimeError('UPDATE_SIGNING_KEY_PEM secret is missing')
    # RUNNER_TEMP is outside checkout; temporary directory defaults to owner-only access.
    with tempfile.TemporaryDirectory(prefix='cqt-sign-', dir=os.environ['RUNNER_TEMP']) as tmp:
        key = pathlib.Path(tmp) / 'key.pem'
        fd = os.open(key, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as f:
            f.write(key_text)
        del key_text
        der = run('openssl', 'pkey', '-in', str(key), '-pubout', '-outform', 'DER')
        if hashlib.sha256(der).hexdigest() != '532bbcd2240dc97094e01333d258eb56f1db2e18417e27739a46aa811c59329b':
            raise RuntimeError('Signing key differs from approved update trust key')
        source = (ROOT / 'src/CodexQuota.Tray/UpdateTrust.cs').read_text()
        modulus = re.search(r'const string Modulus = "([A-Za-z0-9+/=]+)"', source)
        exponent = re.search(r'const string Exponent = "([A-Za-z0-9+/=]+)"', source)
        actual_modulus = run('openssl', 'rsa', '-in', str(key), '-modulus', '-noout').decode().strip().split('=', 1)[1]
        if not modulus or base64.b64decode(modulus[1]).hex().upper() != actual_modulus or not exponent or exponent[1] != 'AQAB':
            raise RuntimeError('Embedded public key differs from signing key')
        pub = pathlib.Path(tmp) / 'public.pem'
        pub.write_bytes(run('openssl', 'pkey', '-in', str(key), '-pubout'))
        run('python3', str(ROOT / 'scripts/sign-update.py'), '--exe', str(exe), '--version', os.environ['RELEASE_VERSION'], '--key', str(key), '--output', str(ROOT / 'dist/update.json'))
        envelope = json.loads((ROOT / 'dist/update.json').read_text())
        payload, signature = base64.b64decode(envelope['payload'], validate=True), base64.b64decode(envelope['signature'], validate=True)
        sig = pathlib.Path(tmp) / 'signature'
        sig.write_bytes(signature)
        run('openssl', 'dgst', '-sha256', '-verify', str(pub), '-signature', str(sig), input=payload)
    print('Manifest signed and verified; private temporary directory removed.')

if __name__ == '__main__':
    try:
        main()
    except subprocess.CalledProcessError:
        raise SystemExit('Signing/verification failed; private diagnostic output suppressed.')
