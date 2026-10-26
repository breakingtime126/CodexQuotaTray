# Isolated QA artifacts — NOT A RELEASE

These files exist only on the qa/update-smoke branch for explicitly authorized Windows validation. They are not published release assets, and there is no production update.json. Do not distribute the candidate as a verified stable update.

- CodexQuotaTray-UpdateSmoke.zip: 1,958,570 bytes; SHA-256 `2e2a61afcb85b37feefa6075154e6479c8ff5884d797c252670c44ccfa0c851f`. Contains a test driver, two isolated fixture executables, unchanged Fetch-Update.ps1, and instructions. Fixture signing keys are generated in memory during testing; no production private key is included.
- candidate/CodexQuotaTray.exe: 2,616,320 bytes; SHA-256 `64985f049f6d1124651e6229faaa9186bda82c908c855d037e2dd8af70ea6534`. Cloud-built 0.2.34 candidate; Windows lifecycle and network acceptance are pending.

Run smoke only as an ordinary user in an isolated temporary directory, following the included README. Do not replace an installed tray with these test fixtures. Production release and signing will occur only after the required validation.
