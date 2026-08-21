# Security policy

## Supported versions

Security fixes are provided for the latest stable LernType release. Preview builds are supported only until the next preview is published.

## Reporting a vulnerability

Use **GitHub → Security → Report a vulnerability** in the [LernType repository](https://github.com/sviat57/LernType/security/advisories/new). Do not include API keys, book text, answers, audio or an unredacted database in a public issue.

Please include the affected version, Windows version, reproduction steps and the smallest redacted sample that demonstrates the issue. Receipt is acknowledged within 72 hours. A confirmed report receives a remediation plan and coordinated disclosure date.

## Security boundaries

- Core practice and progress work locally without an account.
- API credentials are protected for the current Windows user and are never written to diagnostics.
- Online analysis is opt-in per request and uses HTTPS; raw prompts and responses are excluded from application logs.
- Content packages must pass path, size, checksum, compatibility and digital-signature validation before activation.
- ZIP release artifacts include SHA-256 and build evidence includes an SBOM. A distributable MSIX must pass Authenticode verification; unsigned MSIX artifacts are explicitly labelled as short-lived layout-validation evidence.

See [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md) for the maintained threat model.
