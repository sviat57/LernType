# LernType support

## Before opening an issue

1. Record the LernType version and Windows architecture (`x64` or `Arm64`).
2. Note the exact action, expected result and visible error code without copying private learning content.
3. Verify the downloaded ZIP SHA-256. For MSIX, also verify that Windows reports a valid digital signature; packages labelled `unsigned validation` are CI evidence, not stable installers.
4. If requested by a maintainer, share only the relevant event ID, exception type and HResult from `%LOCALAPPDATA%\LernType\Logs\diagnostics.jsonl` after checking the file yourself.

Use a [GitHub issue](https://github.com/sviat57/LernType/issues) for reproducible bugs and a [discussion](https://github.com/sviat57/LernType/discussions) for usage questions. Security and privacy reports belong in a [private advisory](https://github.com/sviat57/LernType/security/advisories/new).

Do not attach `lerntype.db`, API keys, private book text, answers, recordings, the complete profile directory or migration backups. A minimal synthetic sample is sufficient.

LernType targets Windows 11. Windows 10 22H2 is a best-effort ZIP target; treat it as runtime-verified only when the release record contains a separate Windows 10 run.
