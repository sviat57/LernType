# LernType design brief

## Product promise

LernType is a Windows-first, offline-first German learning workspace for Russian speakers. It turns short daily practice, personal texts, local audio, and transparent evidence into a credible path from **Pre-A1** through **C2** without pretending that sparse or self-rated activity equals certification.

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
| Today | choose the next practice | storage loading, offline ready, feature error |
| Learning path | understand current stage and next objective | unpublished skill, no evidence, refreshed evidence |
| Practice | answer word/sentence/text prompts | empty CEFR pool, input-layout hint, feedback |
| Audio | listen, dictate, record, self-review | missing voice, missing microphone, recording, cleanup |
| Exams | inspect format-specific readiness | no attempts, no universal pass, source/caveat |
| Library | extract and practise personal vocabulary | ephemeral draft, saved, oversized, unresolved, delete/export |
| Progress | inspect evidence and reviews due | first-run empty, partial skills, stale/error |
| Settings | control theme/layout/online consent | denied storage, missing layout, consent off |

## Non-goals for 1.0

- Claiming that LernType awards or guarantees Goethe, telc, TestDaF, or DTZ results.
- Sending book text, recordings, or answers online by default.
- Replacing expert-authored full exam simulations with generated approximations.
- Decorative gamification, streak pressure, neon glass, or animation-heavy navigation.
