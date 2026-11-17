# 3. Domain Design

DDD-inspired: aggregates own their invariants, value objects are immutable, and domain logic lives in plain .NET `Core.*` libraries with no MAUI/Android references (as in Use It Before).

## 3.1 Ubiquitous language
| Term | Meaning |
|---|---|
| **Family** | A shared vault. A device can belong to many families. |
| **Member** | A device + person in a family (no accounts; identified by device keys). |
| **Traveler** | A person who travels. They may or may not be a member (kids, grandparents). |
| **Document** | One imported travel item (ticket, booking, visa…), with its parsed fields. |
| **Segment** | One leg: from a place to a place at a time (flight, train, bus) or a stay (hotel) or an event slot. |
| **File version** | One original file attached to a document. Duplicates and updates become versions. |
| **Boarding pass link** | A boarding pass document attached to one segment + one traveler. |
| **Trip** | A confirmed group of documents/segments for a set of travelers. |
| **Trip suggestion** | A trip proposed by the grouping rules, not yet confirmed. |
| **Duplicate candidate** | Two documents that look like the same booking. |
| **Relay** | The ntfy server that carries encrypted messages between family phones. |

## 3.2 Aggregates

### Family (Core.Families)
```
Family
 ├─ FamilyId, Name, CreatedAt
 ├─ RelayEndpoint (server URL, topic)          ← changes on key rotation
 ├─ KeyEpoch (number) + family key (held by Security, not in the aggregate)
 ├─ Members[]  : MemberId, DisplayName, Role(Admin|Member), SigningPublicKey,
 │               AgreementPublicKey, JoinedAt, RemovedAt?, LastSeenAt
 └─ Invites[]  : InviteId, Kind(InPerson|Remote), ExpiresAt, UsedBy?, Status
```
Invariants: at least one admin; only admins invite or remove; a removed member can't be re-added with the same device keys; removal ⇒ new KeyEpoch.

### Traveler (Core.Travelers)
`TravelerId, FamilyId, DisplayName, Aliases[] (PersonName), LinkedMemberId?, DateOfBirth? (optional)`
Invariant: aliases are unique within a family (normalised).

### TravelDocument (Core.Documents), the central aggregate
```
TravelDocument
 ├─ DocumentId, FamilyId, Type, Title (generated, editable)
 ├─ Visibility (Family|Private), OwnerMemberId (uploader)
 ├─ Provider, BookingReference?, Pnr?
 ├─ Segments[]  : SegmentId, Mode(Flight|Train|Bus|Stay|Event|Other),
 │                From: Place, To: Place?, Departs: LocalDateTimeZ, Arrives?: LocalDateTimeZ,
 │                CarrierCode?, ServiceNumber?, Seat?, Class?, Passengers[TravelerId]
 ├─ Passengers[]: TravelerId, RawName (as printed), MatchConfidence
 ├─ IdentityInfo?: DocumentNumber (masked in UI), IssuingCountry, ValidFrom, ExpiresOn   (Visa/Passport)
 ├─ BoardingPassFor?: (SegmentId of the target ticket, TravelerId)                         (BoardingPass)
 ├─ Files[]     : FileVersionId, Sha256, Size, MediaType, OriginalName, AddedAt, AddedBy, IsPrimary
 ├─ FieldConfidence: map field → High|Medium|Low|UserEdited
 ├─ TripId?
 └─ Audit: CreatedAt/By, UpdatedAt/By, Version (HLC), IsDeleted, DeletedAt
```
Invariants:
- exactly one primary file; file SHA-256s are unique within the document;
- a Private document is never published to the relay;
- at least one passenger (if none was parsed, the uploader's traveler is used, D34);
- the segment arrival is not before departure (in UTC);
- user-edited fields are never overwritten by a re-parse or merge.

### Trip (Core.Trips)
`TripId, FamilyId, Name, TravelerIds[], DocumentIds[], StartsAt, EndsAt (derived, can be overridden), Confirmed, Notes`
Status is **derived**, never stored: `Upcoming` (now < start), `Ongoing` (start ≤ now ≤ end + 6 h), `Completed`.

### DuplicateCandidate (Core.Duplicates), local per device
`DocumentA, DocumentB, Strength(Strong|Weak), Reason, Status(Open|Merged|Dismissed)`

## 3.3 Value objects
| Value object | Notes |
|---|---|
| `Place` | `Code?` (IATA airport / station code), `Name`, `City`, `CountryCode`, `TimeZoneId?`. Built from the bundled airport list when a code is recognised. |
| `LocalDateTimeZ` | Local date-time **as printed** + IANA zone if known → `ToUtc()`. Tickets print local times; we never shift them for display. |
| `PersonName` | Normalised form (upper case, accents removed, titles MR/MRS/MS/DR/MSTR removed, "SURNAME/GIVEN" reordered), with token-set comparison. |
| `Pnr` / `BookingReference` | Upper case, alphanumeric 5–10 characters; validation per format. |
| `FileHash` | SHA-256 of the **plaintext** original bytes. |
| `ParsedField<T>` | Value + `Confidence` + source (Barcode, TextRule, Default, User). |
| `HybridTimestamp` | HLC (wall-clock ms + counter + member ID) for conflict resolution. |

## 3.4 Domain services
| Service | Responsibility |
|---|---|
| `DocumentClassifier` | Scores keywords/patterns to choose a `DocumentType` |
| `ITravelDocumentParser` (one per type) | Text + barcodes → `ParseResult` with `ParsedField`s |
| `BcbpDecoder` | IATA Resolution 792 boarding-pass barcode → structured legs |
| `PassengerMatcher` | Raw names → Traveler (exact alias → token-set → fuzzy edit distance ≤ 2); falls back to the uploader |
| `DuplicateDetector` | Exact (hash) and fuzzy (PNR/ref + passengers + date) rules |
| `DocumentMerger` | Merges B into A: files become versions, keeping user-edited and higher-confidence fields |
| `BoardingPassLinker` | Matches a boarding pass to a segment + traveler |
| `TripGrouper` | Builds trip suggestions from segments (see 3.6) |
| `ReminderPlanner` | Documents/trips → a list of `PlannedReminder(kind, fireAtUtc, target)` |

## 3.5 Domain events
`DocumentImported`, `DocumentUpdated`, `DocumentDeleted`, `FileVersionAdded`, `DuplicateDetected`, `DocumentsMerged`, `BoardingPassLinked`, `TripSuggested`, `TripConfirmed`, `TravelerReassigned`, `MemberJoined`, `MemberRemoved`, `FamilyKeyRotated`.

Events are in-process (simple dispatcher). Handlers: **Search** re-indexes, **Reminders** re-plans, **Sync** enqueues an outgoing change, **Trips** re-suggests.

## 3.6 Key rules

**Trip grouping (suggestions)**
1. Take the non-deleted Family-visible segments of the same traveller set, ordered by departure (UTC).
2. Chain segment *n+1* onto *n* when it departs from *n*'s destination city (or the same city's other station/airport) within **4 days** of *n*'s arrival, or when a Stay at that city overlaps the gap.
3. A chain ends when the route returns to the starting city or the gap exceeds 4 days.
4. Chains with ≥ 2 documents become a suggestion named "<main destination city>, <date range>". Documents already in a confirmed trip are excluded.

**Fuzzy duplicate**
- *Strong*: same type AND (same PNR or same booking ref) AND passenger sets overlap AND first departure on the same date (or the stay overlaps).
- *Weak*: same type AND same from/to AND same date AND ≥ 1 shared passenger, with no references available.

**Boarding pass link**: BCBP (PNR, carrier + flight number, Julian date, from/to, passenger name) → the segment whose PNR/flight/date/route match, and the traveler matched by name. Without a barcode, OCR fields are used with the same rule.

**Reminder plan** (local, recomputed on every change)
| Kind | When |
|---|---|
| Departure | 24 h and 3 h before each segment departure / stay check-in / event start |
| MissingBoardingPass | 24 h before a flight segment, per traveler without a linked boarding pass; repeats at 6 h |
| TripTomorrow | 18:00 local time on the day before a trip/segment starts. Shown for my travels, or all family travels (setting). |
| IdentityExpiry | 90 / 30 / 7 days before `ExpiresOn` |
| Backup | 30 days after the last backup |
