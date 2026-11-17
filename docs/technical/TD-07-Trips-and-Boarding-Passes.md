# TD-07 · Trips and Boarding Passes

## Summary
`Core.Trips` groups documents into trips and links boarding passes to tickets.
- **TripGrouper** proposes trips by chaining documents in time order: same travelers, each leg starting where the last one ended, gaps ≤ 4 days, and stays/events in the current city. The chain closes when it gets back home. The user confirms, renames or dismisses.
- **Trip status** (Upcoming / Ongoing / Completed) is computed from the trip's documents, never stored.
- **BoardingPassLinker** attaches a boarding pass to the exact flight leg and traveler, and computes who is still **missing** one.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Trips/Trip.cs` | `Trip` aggregate, `TripView` (trip + documents), `TripStatuses` (status, date ranges) |
| `Core.Trips/TripGrouper.cs` | Suggestions |
| `Core.Trips/BoardingPasses.cs` | `BoardingPassLinker`: link, missing, linked-to |
| `Core.Trips/ITripRepository.cs` + `Storage.Sqlite/SqliteTripRepository.cs` | Persistence |
| `Core.Vault/TripAndSearchServices.cs` (`TripService`) | Use cases: suggestions minus dismissed, confirm, create, rename, add/remove, delete, lists by status |
| `tests/Core.Trips.Tests` (17) + `tests/Core.Vault.Tests` (trip lifecycle) | Tests |

## How it works

### The trip model
- `Trip` holds only the **name, notes and an optional end override**. **Which documents belong to a trip is stored on each document** (`TravelDocument.TripId`) (C-007). With two lists (trip → docs *and* doc → trip), two phones editing at the same time could make them disagree after sync. One pointer per document can't disagree.
- `TripView` = trip + its documents. Start = earliest departure, end = override or latest arrival/departure, travelers = union of all documents' travelers.
- Status: `Upcoming` before the start; `Ongoing` from the start until **6 hours after the end** (a grace period for delays and getting home); then `Completed`.

### Suggestion algorithm (`TripGrouper.Suggest`)
1. Candidates: live documents not in a trip, of transport/stay/event types, with dates. Boarding passes, visas and passports are excluded: they belong to trips through their tickets/travelers, not as legs.
2. Sort by start time.
3. For each document, look for an open chain where:
   - travelers **overlap** (a hotel booked under one name still joins a flight for two);
   - it starts no more than **4 days** after the chain's current end, and not before the chain's start;
   - for transport, it **departs from the chain's current city**; for a stay/event, it is **in the current city** (or the city is unknown).
4. If none fits, transport starts a new chain at its origin. A stay alone never starts a trip.
5. After each transport leg the chain's location moves to its destination. If that equals the origin, the chain is **closed** (home again).
6. Chains with ≥ 2 documents become suggestions named "<first city that isn't home>, <date range>", e.g. **"New Delhi, 12–15 Dec 2026"**, or "28 Dec 2026 – 3 Jan 2027" across years.

Cities are compared by `Place.SameCityAs`: the same city name, or the same airport code. Two Delhi airports count as one city.

Dismissed suggestions are remembered by a stable key (the sorted document ids) in settings, so they don't reappear. After a trip is deleted its documents are free again and can be suggested anew.

### Boarding-pass linking
For a boarding pass's first leg, find a **Flight** document with a segment where:
- the route matches (from/to airport codes), **and**
- the date matches within ±1 day (overnight flights, time-zone differences, and a barcode that has the date but not the time), **and**
- **either** the PNR matches (also checked against the ticket's booking reference) **or** the flight number matches (normalised, so `6E 2345` = `6E2345`; this covers tickets bought through travel agents whose own reference differs from the airline PNR).

The link records `(ticket id, segment id, traveler id)`. The traveler is the boarding pass's passenger if they're on the ticket, else the ticket's only passenger.

**Missing boarding passes:** `MissingFor(ticket, segment, docs)` = travelers on that segment minus travelers with a linked boarding pass for that exact segment. This is **per person per leg**, so a family of four with three passes is told exactly who is missing.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Suggest, user confirms (D19) | Grouping heuristics can be wrong; confirming is one tap | Fully automatic trips (wrong groupings annoy and confuse); manual only (tedious) |
| Chain by city + time + people | Matches how real trips look (out, stay, onward, back) | Date-range overlap only (merges unrelated trips of different family members) |
| Membership on documents (C-007) | Single source of truth under sync | Trip holds document list |
| Status derived, not stored | Never stale; no background job to flip statuses | Stored status updated by a timer |
| Link boarding passes per segment + traveler | Enables precise "missing for Priya on BLR→DEL" | One boarding pass per document |
| PNR **or** flight number | Agency bookings have different references from the airline PNR on the pass | PNR only |

## Edge cases
- Return flight on the same day as hotel check-out → chains (gap 0).
- A long gap (> 4 days) or different people → separate trips (tested).
- A multi-city trip (BLR→DEL→BOM→BLR) → one suggestion (tested).
- A boarding pass for the wrong date or reverse direction → no link (tested).

## Interview Q&A
**Q: How do you detect trips automatically?**
A: A greedy chaining pass over documents in time order. A document joins an open chain if it shares travelers, starts within four days, and continues from the current city (or is a stay or event there). The chain closes when it returns to the origin. Chains of two or more documents are offered as suggestions the user confirms.

**Q: Why store trip membership on the document rather than in the trip?**
A: Sync conflicts. With a single `TripId` per document, last-writer-wins on the document always yields a consistent answer. With membership in two places, concurrent edits could leave a document in a trip that doesn't list it.

**Q: How do you know who is missing a boarding pass?**
A: Each boarding pass is linked to a specific flight segment and traveler. For each upcoming flight segment we subtract the linked travelers from the segment's travelers, and whoever remains gets a reminder 24 h and 6 h before departure.

**Q: Why is status computed rather than stored?**
A: It depends only on time and the documents. Computing it on read means it's always correct, with no timers to keep it updated.

## Change C-018 (2026-11-17): loose documents on the Trips tab
The Trips tab lists confirmed trips **and** journeys that are not in any trip (transport, stays and events with dates; not passports/visas, and not boarding passes already linked to a ticket). Their status uses the same rule as trips (`TripStatuses.Of`: upcoming before the start, ongoing until 6 h after the end, then completed). Before this, a single imported flight never appeared under Upcoming, because a trip needs at least two related documents or a manual "add to trip".
