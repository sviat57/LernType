# Privacy notice

LernType is offline-first. It has no account system, advertising SDK or hidden in-app analytics.

## Data stored on the device

The application may store settings, learning attempts, mastery projections, course progress and installed content-pack metadata. Free-text answers and recordings are not retained by default.

Version 1.3 removes the unfinished personal-library route from the interface and does not create new book projects. A profile upgraded from an earlier version may still contain books that the user explicitly saved. Migration preserves those rows without exposing or uploading them, so a verified rollback does not destroy user data.

The unpackaged build uses `%LOCALAPPDATA%\LernType`. Packaged and unpackaged profiles are discovered through the same migration service and are never silently merged when both contain user data.

LernType 1.3 does not present a one-click full-profile export/reset control. Closing the application before manually copying or removing the profile directory avoids a detached SQLite WAL. Older releases that expose book deletion also remove matching rows from application-managed backups.

## Optional online analysis

Version 1.3 has no public course, exercise or settings route that sends learner text to an online model. An upgraded profile may retain a previously configured OpenAI key encrypted with Windows DPAPI for rollback compatibility; the course does not read or transmit it. Any future return of online analysis requires a separate visible consent flow before a request.

## Microphone

Speaking practice accesses the microphone only after a user action. Each recording stays inside a leased temporary session directory for immediate playback. Prompt changes and session shutdown queue deletion with bounded retry; startup and audio-session cleanup sweeps remove orphaned session directories left by an interrupted process while preserving recordings leased by another running LernType instance. There is no persistent audio-save control.

## Diagnostics

Local diagnostics contain a timestamp, bounded technical event ID, exception type and numeric HResult. They exclude messages, stack traces, file paths, API keys, books, answers, prompts and recordings. Version 1.0 has no telemetry upload and no in-app diagnostic exporter. Any future anonymous diagnostics remain opt-in and use a separate consent screen.

Questions can be opened as a private security advisory or a GitHub discussion without attaching personal learning data.
