# Windows update smoke test artifacts

Testing only, not a production release. Version 0.2.34 remains a development candidate; Windows lifecycle validation is pending. No update.json is published here.

Source: c61f2f572005b67ed8cf06001d5bbd748df2ad5c. Fixes replacement self-lock and adds phase diagnostics.

- CodexQuotaTray-UpdateSmoke.zip: 1,958,813 bytes; SHA256 `223d42201b16b8561605701d5d8e288eb8cc5b8eba917f79b14e1ebc5e40c844`.
- candidate/CodexQuotaTray.exe: 2,616,832 bytes; SHA256 `ea194f5b7efa18babf4966c5096acf9970878e7d8460c1fe9138f4e675642e1e`.

The smoke package contains test-only fixtures and generates its own ephemeral signing key. It does not contain a production private signing key. Run only the isolated test harness as documented inside the archive; the candidate is not yet approved for production distribution.
