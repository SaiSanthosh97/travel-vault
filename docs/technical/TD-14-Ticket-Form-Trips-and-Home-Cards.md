# TD-14 · Ticket Form, Trip Grouping and Home Cards

**Changes:** C-030 (form), C-031 (grouping), C-032 (Home cards). **Decisions:** D63–D66.

## Summary
The app had become hard to understand. Reading worked often, but not always, and when it was unsure it marked values in orange and saved anyway. Feedback was "we are complicating too much". The flow is now three plain steps:

1. **Add or share a ticket.** It opens one short form, pre-filled with what was read. The person confirms or corrects it, then saves.
2. **"Is there a return or onward journey?"** The answer groups tickets into a named trip.
3. **Home shows one flash card per trip.** Tap a card to see the journeys in order, and tap a journey to see its ticket.

## Where the code is
| Part | File |
|---|---|
| The form | `App/ViewModels/ReviewImportViewModel.cs`, `App/Views/ReviewImportPage.xaml` |
| Leg editor (shared with Edit) | `SegmentEdit` in `App/ViewModels/EditDocumentViewModel.cs` |
| Adding tickets, the pending trip | `App/Services/ImportCoordinator.cs` (`AddAsync`, `PendingTripId`) |
| Trip screen | `TripViewModel.Rows` in `App/ViewModels/TripAndSearchViewModels.cs`, `App/Views/TripPage.xaml` |
| Home cards | `HomeViewModel` (`JourneyCard`, `Cards`), `App/Views/HomePage.xaml` |

## How it works

### 1. The form (D63)
- **Always shown.** `ImportCoordinator.ImportAsync` no longer saves "confident" imports straight away (`ImportDraft.CanSaveWithoutReview` is unused by the app).
- **"What is it?"** Flight, train, bus, hotel, event, passport, visa, boarding pass or other. Changing it changes the labels and the legs' `TravelMode` (`SegmentEdit.Mode`, notified so From/To appear or disappear).
  | Kind | Fields |
  |---|---|
  | Journey | From, To (name + optional code), departure date + time, arrival date + time, seat, flight/train no. |
  | Hotel | City, check-in, check-out |
  | Event | Place, start |
  | Passport/visa | Number, country, expiry |
- **Optional for all:** airline / operator / hotel or event name, and PNR / booking reference.
- **Several legs:** one ticket can have several legs (a connecting flight). "＋ Add a connecting journey" pre-fills the new leg's From with the previous leg's To.
- **Who travels:** each printed name gets a picker (family member / new profile / not a traveller). With no name, the picker lists the family with "me" selected.
- **Arrival not read:** it starts equal to the departure, so only the time needs changing. An arrival left equal to the departure is saved as "no arrival".
- **Check on save:** From and To must be filled for journeys, and the place for hotels and events.
- **Confidence:** every field on the form was seen, so it is stored as `High`, or `UserEdited` where the person changed it. Nothing is ever `Low` after the form, which is why nothing is orange (D64). The ticket page no longer colours values, and lists no longer show the "Check" badge.

### 2. Grouping (D65)
```
save ─┬─ PendingTripId set? ── add to that trip → trip screen → "Another ticket for this trip?" ─┬─ Add another → picker
      │                                                                                         └─ That's all
      └─ journey, not in a trip ── "Is there a return or onward journey?"
             ├─ Yes, add that ticket now → name (default "Trip to <destination>") → trip created with this ticket
             │                             → PendingTripId = trip → picker opens
             ├─ Add this to a trip I already have → pick (upcoming/ongoing trips) → added
             └─ No → ticket screen
```
- **Pending trip:** `ImportCoordinator.PendingTripId` remembers the trip being built, so a ticket shared from WhatsApp while the person is adding the return ticket also joins it. It is cleared when they say "That's all", cancel the picker or the form, or add a ticket from Home.
- **Default name:** the ticket's destination ("Trip to Hyderabad"). For a round trip on one ticket (BLR → AUH → LHR → AUH → BLR) it is the far end (London), not home.
- **Trip screen rows** (`TripViewModel.Rows`): documents ordered by start. Transport tickets are labelled **Onward / Return** when there are exactly two, otherwise **Journey 1, 2, 3…**; hotels and events show their type. Each row: route (all stops for a multi-leg ticket), date and time (arrival time only when it's the same day), numbers, seat, PNR, who.
- **Storage:** membership is still stored on each document (`TripId`, C-007), so sync is unchanged.

### 3. Home cards (D66)
- **One card per trip** that isn't completed, plus one per transport/hotel/event ticket that isn't in a trip, sorted by start.
- **Content:** trip name, dates, "3 journeys", and the **next journey**: the first leg that hasn't arrived yet (or the last one if all are under way), with number, seat, PNR and who.
- **Layout:** a horizontal `CollectionView` with `MandatorySingle` snap points, so the cards swipe one at a time. Cards are 320 dp wide in the brand colour, so the next card peeks in.
- **Kept:** trip suggestions (FR-R2) and the ＋ button.

## Key decisions
| Decision | Why | Rejected alternative |
|---|---|---|
| Pre-fill and confirm, every time | One predictable step; reading mistakes are caught before they are saved; no colour code to learn | Save automatically when "confident": the confidence was sometimes wrong, and the orange marks confused people |
| Ask about the return journey right after saving | The person knows the answer at that moment; grouping becomes a natural part of adding | Only automatic trip suggestions: grouped wrongly or too late, and were easy to miss |
| Remember the pending trip in the coordinator | Shared files arrive through the same coordinator, so they join the trip too | Passing the trip id through navigation: lost when a share comes from another app |
| Cards per trip, not per leg | A trip is what people think in ("my Hyderabad trip"); the next leg is on the card | One card per leg: a 4-flight round trip would fill the carousel |
| Reuse `SegmentEdit` for the form and Edit | One leg editor, one set of bugs; the mode now follows the chosen type in both | A separate form model |

## Edge cases
- **A ticket read as a train but actually a bus:** change "What is it?". The legs become bus legs, and train-only details stay under "More details".
- **Nothing read at all:** the form starts with one empty leg and today's date.
- **Passport/visa:** no legs; not asked about trips.
- **Trip with only a hotel:** the trip screen shows "HOTEL"; the card shows check-in as the next item.
- **Cancel on the form while adding to a trip:** that ticket isn't saved and adding to the trip stops.

## Testing
- **Automated:** the parsing and service tests are unchanged and pass. The trip service (`CreateAsync`, `AddDocumentAsync`) was already covered by `Core.Vault.Tests`.
- **On the emulator:** see the check in C-030 to C-032 (CHANGES).

## Interview Q&A
- **Why remove the confidence colours rather than fix them?** The colours tried to share uncertainty after saving. Confirming before saving removes the uncertainty itself, so there is nothing to show.
- **How does a shared file know which trip it belongs to?** The import coordinator holds a "pending trip" while the person is adding tickets to it; every saved import checks it first.
- **Why is trip membership on the document?** One source of truth that merges cleanly in peer-to-peer sync (C-007). The trip holds only its name.

## Possible improvements
- Drag to reorder journeys in a trip.
- Suggest the return ticket's From/To (swapped) when the person adds the return.
- A page indicator under the Home cards.
