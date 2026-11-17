# 10. Risks and Mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **ntfy.sh changes its free policy, rate-limits us, or is down** | Medium | High (no sync) | Relay URL is configurable per family; small batched messages + backoff; self-hosted ntfy guide; the app keeps working offline regardless |
| R2 | **The only phone holding a document goes offline** before anyone receives it (12 h / 3 h retention) | Medium | Medium | Every phone republishes what it holds; background sync every ~15 min; "Waiting for X's phone" status; backup reminders |
| R3 | **Parsing accuracy on worldwide formats** without AI | High | Medium | Barcodes first (BCBP is standardised); generic PNR/date/route rules; confidence highlighting; quick edit; synthetic + real fixtures; no-name → uploader rule |
| R4 | **Encrypted SQLite on Android** (SQLite3MC/SQLCipher packaging or licence issue) | Medium | Medium | Spike first in phase 1; fallback is field-level AES-GCM + in-memory search index |
| R5 | **Android background limits** (Doze, aggressive OEM battery savers on Xiaomi/Oppo/Vivo etc.) delay sync and reminders | High | Medium | Reuse Use It Before's battery-saver checks and guidance; exact alarms for reminders; sync on app open; reminders are local, so they don't depend on sync |
| R6 | **Exact alarm permission** restrictions on Android 12–14+ | Medium | Medium | `USE_EXACT_ALARM` / `SCHEDULE_EXACT_ALARM` handling as in `TimedReminders`; fall back to inexact alarms with earlier lead time |
| R7 | **Time zones**: wrong departure times across zones | Medium | High | Store local time as printed + zone from the airport list; display the printed local time; compute reminders in UTC |
| R8 | **Lost recovery code + all phones lost** | Low | High | Clear warnings; recovery code viewable after unlock; backup reminders; family re-sync covers most cases |
| R9 | **Invite leakage** | Low | High | Remote invites carry no key, need approval + safety code, are single-use and expire in 24 h; removal rotates keys |
| R10 | **Storage growth** (every phone holds every file) | Medium | Low | Show per-family usage; optional "offload completed trips older than N months" (keeps metadata, file re-fetchable from family) |
| R11 | **APK size** (ML Kit models bundled) | Medium | Low | Use unbundled ML Kit models via Google Play services where available (smaller APK), as in Use It Before; trimming/R8 |
| R12 | **Sideloaded APK trust** (no Play Store) | Medium | Low | Signed releases, SHA-256 verified updates, clear install guide |
| R13 | **Concurrent edits** lost by last-writer-wins | Low | Low | Edits are rare; HLC ordering; user-edited fields protected; per-record history could be added later |
| R14 | **Malicious or malformed files** | Low | Medium | Magic-byte checks, size limits, sandboxed PdfRenderer, parser fuzz tests on fixtures |
| R15 | **MAUI Android quirks** (share intents, camera, PdfRenderer bindings) | Medium | Medium | Patterns already proven in Use It Before; thin `Device.*` wrappers with interfaces for testing |
| R16 | **Clock skew** between phones | Medium | Low | Hybrid Logical Clock; never trust remote wall time for ordering alone |
