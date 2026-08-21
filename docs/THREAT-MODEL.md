# LernType threat model

## Assets

- learning history and mastery evidence;
- user-supplied books, answers and recordings;
- optional API credentials;
- built-in and downloaded curriculum/exam content;
- executable and update packages.

## Trust boundaries

1. WPF process ↔ user-selected files.
2. Application ↔ SQLite/profile directory.
3. Application ↔ signed content packages.
4. Application ↔ optional HTTPS analysis provider.
5. Build pipeline ↔ NuGet, GitHub Actions and signing service.

## Principal threats and controls

| Threat | Controls | Verification |
|---|---|---|
| Data loss during upgrade | WAL checkpoint, SQLite backup API, migration journal, atomic promotion, preserved source | interruption/WAL/rollback matrix |
| Progress attached to changed content | immutable semantic keys and content revisions | cross-revision identity tests |
| Zip-slip or malicious content pack | normalized relative paths, size/count caps, SHA-256, RSA-PSS signature | traversal/tamper/oversize tests |
| API-key disclosure | current-user secret protection, redacted logs, no environment echo | settings and diagnostic tests |
| Unintended user-text transmission | offline default, explicit action, size preflight, cancellation | HTTP fake-server tests |
| Stored private book text | temporary default, explicit save, delete/export controls | persistence deletion tests |
| Corrupt or hostile local rows | bounded parsing, quarantine, integrity check, recovery mode | malformed-row fault tests |
| Supply-chain compromise | locked restore, vulnerability gate, dependency-diff license policy, SHA-pinned Actions, SBOM, signed-MSIX release gate | required CI checks |
| Multiple simultaneous instances | named migration/profile mutex | concurrent-start tests |

## Logging policy

Logs may include timestamps, bounded event codes, exception types and numeric HResult values. They exclude exception messages, stack traces, credentials, prompts, answers, book content, dictionary queries and audio paths. Rolling logs are locally capped and are not uploaded by version 1.0.

The model is reviewed before every stable release and whenever a new external service or persisted personal-data category is introduced.
