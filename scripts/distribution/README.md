# Distribution helper templates

These helpers incorporate the Windows compatibility repair: .NET SHA256 and ZipFile replace unavailable PowerShell Get-FileHash and Expand-Archive commands. They only assemble, validate, and extract; they never run the app, change startup settings, or overwrite existing output.

The source files intentionally end in `.template` and contain `__ZIP_SHA256__`. Do not run or distribute them directly. Generate ready-to-use helpers and parts from the exact current release ZIP:

```sh
python scripts/package-split.py artifacts/CodexQuotaTray-win-x64.zip --output artifacts/split-current
```

The generator computes the current archive's SHA256, substitutes it in both helpers, includes Chinese instructions, enforces the <16 MiB attachment size, and verifies byte-exact reconstruction. It refuses to overwrite an existing set of parts. Never mix parts from two releases or copy a previous release's embedded hash.
