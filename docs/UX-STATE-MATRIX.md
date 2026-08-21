# UX state matrix

This matrix is the release checklist for every user-facing state. “Partial” means the feature stays usable but exposes a limitation instead of fabricating a result.

| Surface | Loading | Empty / first run | Error + action | Partial data | Long content | Offline / slow | Permission / device | Keyboard / screen reader | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Shell | thin progress line; shell shown first | Today remains usable | inline retry + technical code | readiness label | responsive compact nav | local-ready state | storage recovery message | named navigation/buttons | Present |
| Learning path | lazy refresh | Pre-A1 shown with zero evidence | inline refresh | Planned/Practice only are distinct from mastered | wrapped descriptors | fully local | locked objectives explain why | 44 px actions + automation names | Present |
| Trainer | busy command state | available-count messaging for small pools | typed command errors | CEFR pool shrinks, never duplicates | scroll container | fully local | keyboard layout hint | focus visuals and localized input layout | Present |
| Audio | device discovery | prompt list always has authored items | inline device/command error | self-rating labelled as such | prompt text wraps | Windows TTS and WAV are local | German voice/microphone guidance | named controls and deterministic tab order | Present |
| Exams | lazy load / refresh | no-evidence readiness | source refresh state | provider-specific “no universal pass” | scrollable sources/modules | bundled metadata local | unavailable exercise families shown | controls named for UIA | Present |
| Library | cancellable extraction/save/export | ephemeral draft guidance | retryable typed errors | unresolved forms stay visible | 500k char hard limit + wrapping | dictionary and extraction local | file access message | button/help text and focus styles | Present |
| Progress | lazy refresh | explicit first-attempt copy | inline refresh | em dash/empty card semantics | scrollable skill list | canonical local events | none required | labeled metrics, no color-only meaning | Present |
| Online analysis | cancellable request | consent-off explanation | timeout/network/protocol messages | heuristic fallback stays distinct | input/output hard limits | disabled by default | explicit consent and API key | cancel button and help text | Present |
| Settings | local load | safe defaults | storage/layout guidance | missing keyboard layouts listed | wrapped explanatory text | fully local except opted-in API | layout install action explains why | tab/focus/high contrast | Present |

## Release checks

- Verify at 1024×768/100%, 1280×720/150%, and 200% scale.
- Verify Windows High Contrast and keyboard-only navigation.
- Verify no raw book text, answers, recordings, API payloads, paths, or account data enter diagnostics.
- Verify microphone recordings are temporary and deleted on reset/shutdown.
- Verify offline mode completes words, sentences, texts, books, audio, progress, and settings without network calls.
