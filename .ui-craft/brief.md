# LernType design brief

## Product promise

LernType is a Windows-first, offline-first German course for Russian speakers. Its primary route teaches **A0/Pre-A1 through A2** with a repeatable rhythm of short theory, writing, reading, listening/speaking, a second rule, a checkpoint and a course-local final exam. B1–C2 remain visible as planned stages. Supplementary drills stay separate from the course, and self-rated activity never masquerades as certified evidence.

## Principles (conflict-resolution order)

1. **Evidence before decoration.** Show measured attempts, data availability, and exam caveats honestly; never invent progress or label practice as an official result.
2. **Local by default.** Core learning, dictionary lookup, books, audio, and history work without an account or network. Any online analysis is explicit, bounded, cancellable, and visibly optional.
3. **One calm next action.** Each surface has a clear primary task, a concise state, and no competing accent actions.
4. **Warm precision.** Premium warm-neutral glass, terracotta accent, sharp typography, deliberate asymmetry, and restrained elevation. Glass is hierarchy, not spectacle.
5. **Windows-native accessibility.** Full keyboard path, visible focus, high-contrast fallback, per-monitor DPI, readable Cyrillic/German text, minimum 44 px controls, and no meaning conveyed by color alone.
6. **Fast feedback, quiet motion.** Typing and frequent controls do not animate. Low-frequency transitions remain subtle and honor system motion settings.
7. **Degraded, never dead.** The shell opens before expensive data work. Loading, empty, partial, permission, offline, and recoverable-error states explain what happened and offer the next action.

## Visual system

- **Style:** premium warm glass with editorial asymmetry; light mode primary, intentionally authored dark mode.
- **Accent:** terracotta (`Color.Accent`); status colors are functional and subordinate.
- **Typography:** Segoe UI Variable Display/Text for native Windows metrics and Cyrillic/German coverage.
- **Density:** medium (5/10); navigation compacts below 1060 DIP, content reflows below 980/900 DIP.
- **Motion:** low (3/10); no decorative entrance animation.
- **Radii:** input 10, button 12, card 18, panel 24, pill only for compact status.
- **Signature detail:** the terracotta bridge/path mark and a route-led home composition.

## Core surfaces and required states

| Surface | Primary task | Required states |
| --- | --- | --- |
| Today | resume the current course or choose a course | storage loading, offline ready, feature error |
| Courses | choose A0, A1 or A2 and see the next unlocked lesson | first run, in progress, passed, planned B1–C2, catalog error |
| Course lesson | complete the six-step teaching rhythm | explanation, strict task feedback, missing voice/microphone, recording, result |
| Course exam | complete the internal end-of-course check | locked, in progress, deterministic score, self-rated speaking evidence, pass/retry |
| Interactive exercises | open optional word, sentence, text, vocabulary-test or audio drills | unavailable module, empty pool, input-layout hint, feedback |
| Progress | inspect evidence and reviews due | first-run empty, partial skills, stale/error |
| Settings | control theme/layout/online consent | denied storage, missing layout, consent off |

## Non-goals for 1.3

- Claiming that LernType awards or guarantees Goethe, telc, TestDaF, or DTZ results.
- Sending book text, recordings, or answers online by default.
- Presenting the internal course exam as a Goethe/telc/TestDaF certificate or official simulation.
- Exposing the unfinished personal-library workflow in public navigation; legacy rows remain preserved for migration and rollback.
- Decorative gamification, streak pressure, neon glass, or animation-heavy navigation.
