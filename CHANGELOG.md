# Changelog

All notable changes follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and Semantic Versioning.

## [1.2.0] — 2026-08-22

### Added

- A cohesive 45-icon vector family and a reproducible multi-resolution LernType bridge mark.
- Purposeful 80/120 ms button and switch feedback that follows Windows motion and High Contrast settings.

### Changed

- Rebuilt every application surface around a warm premium glass design system with intentional light and dark themes.
- Improved responsive layouts at 1180×760, 820×600 and the supported minimum of 720×520.
- Standardized buttons, toggles, choices, fields, progress, focus rings, status pills and scroll treatments.
- Restored concise UI Automation names for visual list choices while preserving all practice behavior.

## [1.1.0] — 2026-08-22

### Added

- Dedicated study centre for every level from Pre-A1 through C2 with independent, level-scoped modules.
- Safe Russian vocabulary aliases and one-edit typo tolerance with explicit learner feedback.
- Twenty-eight original bilingual passages, bringing the offline catalog to five texts per level.

### Changed

- Word and sentence sessions remain in their selected practice unit until completion.
- SQLite schema v3 stores localized accepted answers while preserving existing progress and books.
- Schema upgrades now require a clean foreign-key check; legacy orphan book-word rows are retained
  in the verified pre-upgrade backup and represented by metadata-only quarantine records.

## [1.0.0] — 2026-08-21

### Added

- Canonical append-only learning evidence, mastery projections and versioned spaced repetition.
- Provider-specific exam scoring and source-backed exam blueprint fields.
- Signed offline content-package verification with checksums, compatibility gates and rollback.
- Safe data-root migration, recovery journal, backups and stable semantic content identity.
- Explicit book privacy controls, cancellation and bounded online analysis.
- .NET 10, x64/arm64 release pipeline, SBOM, CodeQL and package vulnerability gates.
- Responsive premium glass shell and professional LernType application icon.
- Local listening/dictation practice plus temporary speaking record/playback with explicit self-rated evidence.
- Lazy feature initialization, typed asynchronous command errors and a local progress dashboard.
- Reproducible x64/Arm64 ZIP publishing, MSIX manifest validation and certificate-gated MSIX/App Installer tooling.

### Changed

- The internal beginner stage is named `Pre-A1` rather than presenting A0 as an official CEFR certificate level.
- Historical aggregate progress is separated from fresh evidence and cannot create synthetic mastery.

### Security

- Dependency restore is locked and audited transitively.
- Release workflows pin third-party GitHub Actions by commit SHA.

## [0.2.0] — 2026-08-20

- Initial public LernType release with word, sentence, text, grammar, book and vocabulary-test modes.
