# TD-03 · Domain Model: Families, Travelers, Documents

## Summary
Three aggregates carry the business rules:
- **Family** (`Core.Families`): who is in a shared vault and who may change membership.
- **Traveler** (`Core.Travelers`): the people who travel, plus the name matching that connects printed passenger names to them.
- **TravelDocument** (`Core.Documents`): one imported ticket or booking with its legs, passengers and file versions, plus the merge and sync-conflict rules.

All three are plain C# classes with no database or UI code, implement `IVersioned` for sync, and are unit-tested (57 tests).

## Where the code is
| File | Purpose |
|---|---|
| `Core.Families/Family.cs` | `Family`, `Member`, `MemberRole`, membership rules, topic generation |
| `Core.Families/Invites.cs` | `InPersonInvite`, `RemoteInvite`, `InviteCodec` |
| `Core.Families/IFamilyRepository.cs` | Persistence contract (+ wrapped family keys) |
| `Core.Travelers/PersonName.cs` | Name normalisation |
| `Core.Travelers/Traveler.cs` | Traveler profile + aliases |
| `Core.Travelers/PassengerMatcher.cs` | Printed name → traveler |
| `Core.Documents/ValueObjects.cs` | `DocumentType`, `Visibility`, `Confidence`, `Place`, `LocalDateTimeZ`, `IdentityInfo`, `BoardingPassLink` |
| `Core.Documents/TravelDocument.cs` | `TravelDocument`, `Segment`, `FileVersion`, `DocumentPassenger` |
| `Core.Documents/DocumentRules.cs` | `DocumentTitles`, `BookingCodes`, `DocumentMerger`, `DocumentConflicts` |

## How it works

### Family aggregate
- `Family.Create` makes the creator an **Admin**, generates an unguessable **relay topic** (`"tv"` + 32 random base62 characters ≈ 190 bits) and starts at **key epoch 1**.
- Invariants enforced in methods (they throw `FamilyRuleException` with a user-readable message):
  - only admins add, remove, rename or change roles (a member may remove *themselves*, i.e. leave);
  - at least one admin must remain while other members exist;
  - a removed member can never rejoin with the same device keys (it must reinstall and be invited again);
  - two members can't share one signing key.
- `RemoveMember` **increments the epoch and changes the topic**. The application layer then creates a new family key and sends it to each remaining member (TD for sync). The removed phone keeps listening to the old topic with the old key and receives nothing new.
- The family **key** is deliberately *not* a property of `Family`. Aggregates get serialised for sync and storage, and a secret must never travel with them by accident. Keys go through `IFamilyRepository.SaveKeyAsync` already wrapped by the key ring.

### Invites
Two kinds (approved design point 1):
| | In-person (QR) | Remote (text) |
|---|---|---|
| Contains family key? | **Yes** | **No** |
| Lifetime | 15 minutes | 24 hours |
| Joining | Instant | Joiner sends keys → both compare 6-digit safety code → admin approves → family key sent sealed to the joiner |
| Why safe | Both people physically present; short-lived | Leaked text alone is useless |

`InviteCodec` encodes as `tv1:q:` / `tv1:r:` + base64url(JSON). `Decode` scans *any* pasted text word by word, because people forward the whole WhatsApp message. It rejects expired invites, malformed data, non-HTTPS relays and wrong key lengths.

### Name normalisation (`PersonName`)
Tickets print names in many styles. Parsing steps:
1. Remove accents (`José` → `JOSE`) using Unicode decomposition (FormD) and dropping combining marks.
2. Upper-case.
3. Airline style `SURNAME/GIVEN` → `GIVEN SURNAME`, including titles glued on by boarding-pass barcodes (`SHARMA/RAHULMR` → `RAHUL SHARMA`, only if ≥ 3 letters remain, so `LEE/AMY` is safe).
4. `Surname, Given` → `Given Surname`.
5. Split into letter-only tokens. Apostrophes are joined (`O'Brien` → `OBRIEN`). Titles are dropped (MR, MRS, MS, DR, MSTR, SHRI, SMT, CHD, INF…).

### Passenger matching (`PassengerMatcher`)
Strength levels, strongest first:
| Strength | Rule | Example |
|---|---|---|
| Exact | Same normalised name or the same set of words | `SHARMA/RAHULMR` ↔ "Rahul Sharma" |
| Good | One side's words ⊂ the other's, with ≥ 2 full words in common | `RAHUL KUMAR SHARMA` ↔ "Rahul Sharma" |
| Weak | Every word corresponds by initial or a small typo (edit distance 1, or 2 for words of 7+ letters) and ≥ 1 word is exact | `R SHARMA`, `RAHUL SHARNA` |
| None | Otherwise | `SHARMA` (one word can't identify a person) |

- **Ambiguity rule:** if two travelers tie for the best strength, *no one* is chosen. A wrong automatic assignment is worse than asking.
- `IsConfident` (Good or Exact) decides whether the app assigns silently or asks for confirmation.
- When the user confirms a match, the printed form is stored as an **alias** (`Traveler.AddAlias`), so the next ticket matches **Exact**. The app learns each family's naming quirks.
- Edit distance is the classic Levenshtein dynamic programme, using two rows (O(n) memory).

### TravelDocument aggregate
- **Segments** model legs: a flight with a connection is one document with two segments. A hotel is one `Stay` segment (`From` = hotel, `Departs` = check-in, `Arrives` = check-out).
- **Times** are `LocalDateTimeZ`: the local time **as printed** plus an IANA time-zone id (from the airport list). `ToUtc()` converts only for ordering and reminders. Inside a DST gap it moves to the first valid time, and an unknown zone falls back to the phone's zone.
- **Passengers:** `SetPassengers([], uploaderTravelerId)` assigns the uploader when no name was printed (decision D34), with `Confidence.Low` so the UI invites a correction.
- **Files:** `AddFile` refuses the same SHA-256 twice, keeps exactly one primary, and `makePrimary` moves the primary flag (used by merges).
- **FieldConfidence** maps field name → Low/Medium/High/**UserEdited**. `MarkEdited` is called by every manual edit, and automatic processes never overwrite a `UserEdited` field.
- `Validate()` lists rule violations (one primary file, unique hashes, at least one traveler, arrival ≥ departure, boarding pass has a flight).

### Merging duplicates (`DocumentMerger`)
Used when the same booking arrives twice with different files (e.g. a re-issued e-ticket):
1. The source's files are added to the target as **versions**. If the source has the newest file, that file becomes primary.
2. Segments are taken from the newer document (new times/seats), unless the target's segments were edited by the user.
3. Provider/PNR/booking ref: an empty field is filled, or replaced if the source is more confident.
4. Passengers are united. Notes are concatenated without duplicates.
5. The source becomes a **tombstone** with `MergedInto = target.Id`. Because it's a normal versioned delete, the merge replicates to other phones like any edit.

### Sync conflicts (`DocumentConflicts.Resolve`)
Whole-record **last-writer-wins** by HLC version, with one protection: if the local copy has a `UserEdited` field and the incoming copy's value for that field was *not* user-edited, the local value is kept. This covers the case of a merge or re-parse on phone B arriving after a manual correction on phone A.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Travelers separate from members | Kids and grandparents travel but don't have the app; one member may book for many | Passengers = app users only (excludes most travellers) |
| Learned aliases | Each family has recurring naming quirks; after one confirmation, future tickets match exactly | Fuzzy matching only (never improves) |
| Refuse ambiguous matches | Wrong silent assignment → wrong reminders/boarding-pass checks | Pick the first candidate |
| Segments inside documents | Connections and returns are common; boarding passes are per leg | One journey per document |
| Local time + zone | Tickets print local times. Showing converted times confuses travellers. Reminders need the real instant. | Store UTC only (loses "as printed"); store local only (wrong reminders across zones) |
| Confidence per field + UserEdited | Lets the UI highlight what to check, and protects human corrections from automation | A single document-level confidence |
| Merge = versions + tombstone | Nothing is lost; the merge is just another replicated change | Physical delete of the duplicate (doesn't replicate, loses files) |
| Family key outside the aggregate | Secrets must never be serialised by accident | Key as a property |

## Edge cases
- Surname only (`SHARMA`) → no match (a single word can't identify a person).
- Twins `Arjun Mehta` / `Arun Mehta` vs `A MEHTA` → no match (ambiguous).
- Last admin leaving while others remain → refused with a clear message.
- Merging a document with itself or across families → refused.
- An invite pasted with surrounding chat text → still found.

## Testing
- `Core.Families.Tests/FamilyTests.cs` (13): roles, last-admin rule, removal → epoch/topic rotation, no rejoin, invite round trips, expiry, HTTPS-only, no family key in remote invites.
- `Core.Travelers.Tests/PassengerMatcherTests.cs` (27): ten printed name styles, every strength level, ambiguity, alias learning, deleted travelers, edit distance.
- `Core.Documents.Tests/TravelDocumentTests.cs` (17): no-name → uploader, file versions, validation, time-zone conversion, titles vs user edits, booking-code normalisation, masking, merges, conflict resolution.

## Interview Q&A
**Q: What makes `TravelDocument` an aggregate root?**
A: It's the consistency boundary: segments, passengers and file versions are only changed through it, and its invariants (one primary file, at least one traveler, valid times) hold after every operation. It's also the unit of storage and sync: one record, one version.

**Q: How do you handle the same booking imported twice?**
A: Identical bytes (same SHA-256) are skipped before import. Same PNR + passengers + date but a different file is offered as a merge: files become versions, the newest is primary, user edits are preserved, and the duplicate becomes a tombstone pointing at the survivor so the merge replicates.

**Q: Why last-writer-wins instead of a CRDT?**
A: Edits to the same document on two phones at the same moment are rare in a family, and the data is small and human-checkable. LWW with HLC is simple and predictable. The one real risk, automation overwriting a human fix, is covered by the UserEdited protection.

**Q: How does name matching avoid false positives?**
A: It requires at least two full words for subset matches, at least one exact word for fuzzy matches, never matches a single word, and refuses ties. Uncertain (Weak) matches are only suggestions that the user confirms.

**Q: Why do remote invites not include the key?**
A: Text messages get forwarded, backed up and screenshotted. If the text included the key, anyone who saw it could read the vault. Instead it carries a one-time secret and the admin's public key; the actual key is sent only after a human-verified safety code.

## Possible improvements
- Phonetic matching (e.g. Double Metaphone) for transliterated names (Mohammed/Muhammad).
- Field-level LWW (per-field versions) if concurrent edits become common.
