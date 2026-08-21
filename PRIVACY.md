# Privacy notice

LernType is offline-first. It has no account system, advertising SDK or hidden in-app analytics.

## Data stored on the device

The application may store settings, learning attempts, mastery projections, installed content-pack metadata and books that the user explicitly saves. A pasted book remains temporary until **Save project** is selected. Free-text answers and recordings are not retained by default.

The unpackaged build uses `%LOCALAPPDATA%\LernType`. Packaged and unpackaged profiles are discovered through the same migration service and are never silently merged when both contain user data.

Users can export an individual saved book, delete it, or clear all saved books. Book deletion covers its active SQLite rows and matching rows in application-managed backups. LernType 1.0 does not present a one-click full-profile export/reset control; closing the application before manually copying or removing the profile directory avoids a detached SQLite WAL.

## Optional online analysis

OpenAI-based feedback is disabled until the user configures it. Before a request, LernType identifies the destination service and enforces request-size and response-size limits. Only the text submitted for that action is transmitted. Requests use `store=false`; provider processing remains governed by the provider’s terms and privacy policy.

## Microphone

Speaking practice accesses the microphone only after a user action. Version 1.0 keeps each recording inside a leased temporary session directory for immediate playback. Prompt changes and session shutdown queue deletion with bounded retry; startup and audio-session cleanup sweeps remove orphaned session directories left by an interrupted process while preserving recordings leased by another running LernType instance. It has no persistent audio-save control.

## Diagnostics

Local diagnostics contain a timestamp, bounded technical event ID, exception type and numeric HResult. They exclude messages, stack traces, file paths, API keys, books, answers, prompts and recordings. Version 1.0 has no telemetry upload and no in-app diagnostic exporter. Any future anonymous diagnostics remain opt-in and use a separate consent screen.

Questions can be opened as a private security advisory or a GitHub discussion without attaching personal learning data.
