# TD-06 · Import Flow, Duplicates and the Application Layer

## Summary
`Core.Vault` is the **application layer**: the use cases that coordinate domain components, storage and devices. `ImportService` takes a shared file all the way to a saved, encrypted, searchable document in two steps:
- `PrepareAsync`: parse, then build a draft for the review card;
- `SaveAsync`: commit, after the user confirms or immediately when nothing needs review.

Alongside it are `DocumentService` (edit, visibility, delete/restore, merge, open files), `TravelerService`, `TripService` and `SearchService`. Changes are announced through `VaultEvents`, so search, sync and reminders react without the use cases knowing about them. `Core.Duplicates` provides the fuzzy-duplicate rules.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Vault/Abstractions.cs` | `VaultSession` (who/keys/clock), `ISettingsStore`, `IFileKeyStore`, `IFileRequester`, `VaultChange`, `IVaultChangeHandler`, `VaultEvents` |
| `Core.Vault/ImportService.cs` | `PrepareAsync`, `SaveAsync`, `ImportDraft`, `NameDecision` |
| `Core.Vault/DocumentService.cs` | Edit, reassign, visibility, delete/restore/purge, merge, primary file, open/receive/read files, next-up |
| `Core.Vault/TravelerService.cs` | Profiles, aliases, "my traveler" |
| `Core.Vault/TripAndSearchServices.cs` | Trips (TD-07) and search (TD-08) |
| `Core.Duplicates/DuplicateDetector.cs` | Strong and weak duplicate rules |
| `tests/Core.Vault.Tests` | 12 use-case tests on a **real encrypted database** with fake OCR |

## How it works

### PrepareAsync (nothing is saved yet)
```
bytes ──size ≤ 25 MB?──▶ type from magic bytes ──▶ SHA-256
   │                                                   │
   │                       already in this family? ◀───┘ → AlreadyInVault (stop, show "Already in vault: <title>")
   ▼
ContentExtractor (text + barcodes) → TravelDocumentParser → ParsedDocument
   ▼
PassengerMatcher for each printed name (against the family's travelers)
   ▼
Draft TravelDocument (type, refs, segments, identity, confidences)
   ▼
DuplicateDetector against live family documents → Strong / Weak candidates
   ▼
ImportDraft { Document, Content, Names, Duplicates, CanSaveWithoutReview }
```
`CanSaveWithoutReview` is true only when parsing needs no review, every name matched confidently, and there are no duplicates. The app can then save silently and show a "Saved" toast. Otherwise the review card appears.

### SaveAsync
1. **Travelers.** For each `NameDecision`: use the chosen traveler, or create a new profile (name title-cased: `MR RAHUL SHARMA` → "Rahul Sharma"), or ignore the name. Any non-exact match the user accepted is **learned as an alias** (TD-03).
2. **No names → the uploader** (decision D34) via `MyTravelerAsync`.
3. **The file:** encrypted by `FileVault` with a new random key. The key is wrapped by the key ring (label `file/<id>`) and stored in `file_keys`. The `FileVersion` records SHA-256, size, media type and original name.
4. **Boarding pass:** linked to its ticket (`BoardingPassLinker`, TD-07).
5. **Title** generated (unless edited).
6. **Merge or save:** if the user picked "Merge" on a duplicate prompt, `DocumentMerger` folds the new document into the existing one (TD-03). Otherwise it's saved as new.
7. **HLC version stamp** (`session.Stamp`), then save, then publish `VaultChange`.

### Exact vs fuzzy duplicates (`DuplicateDetector`)
| Kind | Rule | UX |
|---|---|---|
| **Exact** | Same SHA-256 of the original bytes in this family | Not imported: "Already in vault" |
| **Strong** | Same type AND (PNR or booking ref equal, cross-matching PNR↔ref too) AND travelers overlap (or unknown) AND same start day (or overlapping hotel stay) | "Looks like an updated version. Merge?" |
| **Weak** | Same type AND at least one side has no reference AND same people, day and route | Dismissible hint |
| Passport/visa | Same document number | Strong |

Boarding passes are **never** duplicates of tickets (different type). They get linked instead.

### VaultEvents (in-process domain events)
Every use case ends with `events.PublishAsync(new VaultChange(familyId, type, id, FromSync))`. Handlers:
- `SearchService` re-indexes the document (or the whole family when a traveler is renamed);
- the sync outbox (TD for sync) queues local changes for the relay;
- the reminder planner (app) re-plans;
- the UI refreshes.

A failing handler is reported through `HandlerFailed` but **doesn't stop the others or the use case**. The data is already saved, and search can be rebuilt with `ReindexFamilyAsync`.

### Opening a file (D51)
`OpenFileAsync` unwraps the per-file key and decrypts. If the phone doesn't have the file yet, it calls `IFileRequester.RequestAsync` (sync asks family phones immediately) and returns `Requested`, so the UI shows "Getting it from family…". `StoreReceivedFileAsync` saves the file when it arrives, re-encrypted with a fresh local key.

### "My traveler" (and a bug the tests caught)
`MyTravelerAsync` returns the profile linked to this phone's member. Originally it was created on every save, which produced a **second** "Rahul Sharma" profile next to the one created from the ticket. Every later ticket then matched two profiles equally, the matcher refused to guess, and a third profile was created. The trip-suggestion use-case test exposed it (the three trip documents had three different travelers). Fix (C-008): create it only when a document has no names, and **adopt an existing unlinked profile with exactly my name** instead of creating a duplicate.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Use cases in a plain library (`Core.Vault`) (C-005) | Testable on a PC with a real database; the MAUI app stays thin | Services inside the app project (Android-only, can't be unit-tested) |
| Two-step import (Prepare → Save) | The user can review before anything is stored; nothing to clean up if they cancel | Save immediately and edit afterwards (leaves junk on cancel, syncs wrong data to family) |
| Exact duplicates by SHA-256 *before* parsing | Instant and certain; the same WhatsApp PDF forwarded by two people is the most common duplicate | Parse first (wastes time) |
| Fuzzy duplicates as suggestions, never automatic | A wrong automatic merge loses information; the user decides | Auto-merge |
| In-process events instead of direct calls | Open/closed principle: adding sync or reminders didn't change ImportService | ImportService calling search, sync and reminders directly (tight coupling) |
| Handler failures isolated | A search-index hiccup must never lose an import | Let exceptions propagate |
| Per-file key wrapped by the key ring | See TD-02; plus `file_keys` doubles as "this phone has the file" | A separate "present" flag |

## Edge cases
- Same file shared again → AlreadyInVault (tested).
- Updated e-ticket (same PNR, new time) → Strong duplicate → merge → 2 versions, new time (tested).
- No passenger name → uploader (tested).
- Boarding pass photo after the ticket → linked; "missing boarding pass" cleared (tested).
- Unsupported bytes → Unsupported (tested).
- File missing on this phone → requested from family (tested).
- Renamed traveler → old name kept as an alias, search updated (tested; this also caught a bug where the alias was added *before* the rename and was therefore refused as "already known").

## Testing
`tests/Core.Vault.Tests` builds a **complete vault in a temp folder** (`TestVault`): an encrypted SQLite file, an encrypted file vault, the real parsers, and fake OCR/barcode readers that map an image's bytes to prepared text. 12 tests cover import, exact and strong duplicates, merge, no-name, boarding-pass linking, unsupported files, missing files, edits, delete/restore, reassign, privacy rules, next-up, renaming, and the full trip lifecycle.

## Interview Q&A
**Q: Walk me through what happens when a user shares a PDF from WhatsApp.**
A: The share intent hands us the bytes. We detect the type from the bytes, hash them and check for an exact duplicate in the family. Then we extract text (PDF text layer, or OCR of rendered pages) and barcodes, parse it into a draft with per-field confidence, match printed names to travelers, and look for fuzzy duplicates. If everything is confident we save straight away; otherwise the user sees a review card. Saving creates any new traveler profiles, learns name aliases, encrypts the file with its own key, links boarding passes, stamps an HLC version, and publishes a change event that updates search, queues sync and re-plans reminders.

**Q: How do you test use cases that involve OCR and a database?**
A: The application layer depends only on interfaces. In tests we use the *real* encrypted SQLite storage and file vault in a temp folder, and fake only the device parts: OCR and the barcode scanner return prepared results for a given image. That gives high-fidelity tests that run in seconds without a phone.

**Q: Why not merge duplicates automatically?**
A: Two bookings can share a PNR legitimately (e.g. split itineraries), and an updated e-ticket may change times. Merging is cheap for the user (one tap) but expensive to undo if wrong, so we suggest and let them decide. Exact byte-duplicates are the only automatic case, because they're provably identical.

**Q: What's the role of the domain events?**
A: They decouple side effects from use cases. Import doesn't know search, sync or reminders exist; each subscribes to `VaultChange`. That made it possible to add sync later without touching import, and each handler can fail independently.

## Possible improvements
- Batch import (several files shared at once) with one combined review screen.
- Undo for merges (keep the source tombstone's data for 30 days, which is already stored).
