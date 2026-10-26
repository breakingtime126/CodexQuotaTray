# Automatic updates

CodexQuotaTray checks the fixed public repository `breakingtime126/CodexQuotaTray` for stable updates. The initial updater-enabled EXE must be distributed once. Later releases are downloaded, verified, replaced at the same executable path and restarted automatically. Source, theme, startup and CLI preferences are preserved. The right-click menu exposes `自动更新` and `检查更新`; automatic checks use a six-hour interval with a 15-second startup delay.

## Release contract

- Repository: `https://github.com/breakingtime126/CodexQuotaTray`
- Stable release tag: four-component version, for example `v0.2.34.0`.
- Required assets: `CodexQuotaTray.exe` and `update.json`.
- The signed manifest pins application ID, stable channel, version, exact versioned asset URL, size, SHA-256 and publication timestamp.
- Updates must be signed by the release key whose **public** component is compiled in `UpdateTrust.cs`. Never commit private keys, tokens, local account data or update caches.
- Prepare a draft release, attach both assets, verify them, and only then publish. Do not use prerelease for the production latest channel. Enable immutable releases if approved so published tags/assets cannot silently change.
- Generate the manifest with `python3 scripts/sign-update.py --help`; supply an existing private signing key outside this repository. This application-level update signature is not a Windows Authenticode certificate and does not remove SmartScreen warnings. Signing is not publication. Keep the signing private key backed up securely; loss prevents updates to already-deployed clients.

## Installation safety

The application never executes an unsigned/hash-mismatched update. The transport prefers the installed fixed-path PowerShell 7, otherwise the Windows built-in PowerShell 5.1. It uses ordinary HTTPS certificate checks, no credential forwarding, no proxy bypass and no elevation. It stages files beside the installed EXE, so the directory must be writable. The helper verifies the live parent process identity, waits for its normal shutdown, makes a same-volume replacement with a rollback copy, and starts the new image from the original path. The new image must confirm that its tray initialized. Failed replacement restarts the original image where possible. Failed new startup restores a verified backup and suppresses that version until a newer release is available.

Automatic installation is disabled while the process is elevated; run it normally. Read-only directories, file locks, missing runtime/transport, network failures and invalid feeds keep the current version. Update failures are exposed in the check-update menu tooltip; manual checks show the reason. Unknown files and reparse points are never swept during cache cleanup. Rollback copies are limited by safe cleanup on subsequent checks; currently locked files can remain until a later check.

The rollback handshake confirms process/tray startup, not correctness of every feature. Release testing remains necessary. Existing versions without the updater cannot acquire this feature automatically.

## Windows verification

`tools/UpdateSmoke` builds isolated net48 fixture processes linked to the actual replacement helper. It covers successful replacement, corrupt package rejection before shutdown, locked replacement recovery, failed-start rollback, transient health-marker locking and a 65-second delayed parent shutdown. Test signing keys are generated in memory; only the public fixture key is written. Tests do not use the production signing key, modify the installed tray, change startup settings or publish releases. Real PowerShell download and the full WinForms application still require supported Windows validation.

On 2026-10-26, the corrected six-case lifecycle suite passed 6/6 on real Windows as a normal user, with the installed application untouched. This verifies the shared replacement helper using isolated fixtures, not a live end-to-end WinForms update or production network download. See [0.2.34 verification](VERIFY-0.2.34.md).

## 0.2.36 检查频率

自动更新默认开启，启动约 15 秒后首次检查，之后每次检查结束约 10 分钟再检查；旧版本保存的 6 小时等待时间不再阻止启动检查。发现新版继续自动下载、校验、安装并重启，不增加点击确认步骤。手动关闭的状态保持关闭，下载中关闭也不会继续自动安装；手动“检查更新”仍可主动更新。
