# TD-13 · Reading Tickets as Forms, and the Ticket Card

## Summary
The first release read tickets as **plain text**: "find the label, take the rest of the line". Real Indian tickets broke that. The 2023+ IRCTC/RailOne slip prints a row of labels (`PNR: | Train No./Name: | Class:`) with the values on the **row below**. App screenshots put a small "PNR" over a big number. Older slips pack **three pairs on one line**, and agent slips **wrap** values onto the next line. One RailOne ticket reached the user's phone and nothing useful was read.

This change (C-022) teaches the parser to read a ticket **the way a person does: by position**. Every word now comes with its box: from the PDF text layer (PdfPig), from OCR (ML Kit element boxes), or from character columns for plain text. A new `FormReader` finds known labels, then looks for each one's value **to its right** or **below it, in the same column**. Values it finds become *form fields*, and every existing rule checks those before falling back to the old text rules.

On top of that, the parser gained provider knowledge: IRCTC/RailOne slips in three layouts, including the passenger table and berth codes like `CNF/S4/56/SU`. It also learned IndiGo boarding passes and itineraries, MakeMyTrip-style e-tickets, redBus and Booking.com.

The document screen was redesigned as a **ticket card** (C-023). It shows the provider and status, big codes and times for both ends, a perforated strip with PNR / coach / berth / class, and the passengers with their seats. Everything else is folded under "More details".

## Where the code is
| File | Purpose |
|---|---|
| `Core.Parsing/TextLayout.cs` | `LayoutWord` (text + box), rows built by vertical centre, cells split at wide gaps; `FromGrid` for plain text |
| `Core.Parsing/FormReader.cs` | Label dictionary and the four pairing rules (right of, below, stacked, wrapped) → `FormField` list |
| `Core.Parsing/TextFacts.cs` | `Fields`, `FieldValue`, and `LabelValue` / `DateTimeNear` / `PersonNames` now check form fields first; `FirstTime` (12/24-hour) |
| `Core.Parsing/ContentExtractor.cs` | `PdfWords` (PdfPig word boxes, y flipped to top-down), `ITextRecognizer.ReadWordsAsync` (default method), `WithLayout` |
| `Device.Vision/Android/MlKitReaders.cs` | ML Kit `Text.Element` bounding boxes → `LayoutWord`s |
| `Core.Parsing/TravelDocumentParser.cs` | Train/bus rewrite (`DateAndTime`, `GroundRoute`, `RailCity`, `Train`, `RailPassengers`, `DescribeRailStatus`, details), flight fallbacks (`LabelledAirportRoute`, labelled departure time, `FlightDetails`), hotel details |
| `Core.Documents/TravelDocument.cs` | `Segment.ServiceName` (train name) and `TravelDocument.Details` (`DocumentDetail` label/value list) |
| `App/ViewModels/DocumentViewModel.cs` + `Views/DocumentPage.xaml` | The ticket card |
| `samples/synthetic/irctc-ers-*.txt`, `indigo-*.txt`, `makemytrip-flight.txt`, `redbus-ticket.txt`, `bookingcom-hotel-columns.txt` | Layouts copied from public sample tickets, **values made up** |
| `tests/Core.Parsing.Tests/ProviderLayoutTests.cs`, `FormReaderTests.cs` | 21 new tests, including one that **writes a real PDF** with PdfPig and reads it back |

## How it works

### 1. Every word gets a position (`TextLayout`)
- **PDF:** `page.GetWords()` gives each word's bounding box in PDF points, with y measured **upwards**. It is flipped (`page.Height - box.Top`) so that y always grows downwards, as on screen and in OCR.
- **OCR:** ML Kit returns blocks → lines → **elements** (words), each with a `BoundingBox` in image pixels. `ReadWordsAsync` keeps them.
- **Plain text** (a shared SMS or email, and every test fixture): `FromGrid` treats each character as one unit wide and each line as one unit high. Monospaced text that lines values up under headers therefore keeps its columns.
- **Rows:** words are sorted by vertical centre. A word joins the current row if its centre is within half a line height of the row's centre. OCR boxes on a slightly tilted photo still land in one row.
- **Cells:** within a row, a gap wider than **1.5 × the average character width** starts a new cell. "AJMER JN (AII)" stays one cell; the next column's value is a new one. Using character width rather than points makes one rule work for 9-pt PDFs, 2000-pixel screenshots and character grids.

### 2. Finding labels (`FormReader.AnalyzeCell`)
- One regex alternation of about 90 label patterns (rail, airline, travel site, bus, hotel, generic), **most specific first**: "Train No. & Name" must win over "Name", and "Scheduled Departure" over "Departure".
- Labels are **strong** (distinctive: "Boarding Time", "Quota", "PNR") or **weak** (common words: "To", "Class", "Date", "Name"). A match counts as a label when it:
  - has a colon, or
  - starts the cell, or
  - is strong, or
  - sits in a cell that began with a colon-less label. That last case is a run like `Name Mr X From Dibrugarh To Kolkata Flight No. 6E 206`.
- Guards against false labels:
  - a weak word starting a phrase with nothing else labelled ("from 14:00", "To view your ticket…") is just text;
  - a colon-less label followed by more than six words is a sentence.
- A cell that starts with an unknown `Something:` is also treated as a label, so new providers still pair up.

### 3. Pairing labels with values (`FormReader.Read`)
For each label without a value:
1. **Right of it:** the next free (non-label) cell on the same row, before the next label. Example: `Status:  Confirmed`.
2. **Stacked block:** K rows that each hold one bare label, followed by K rows that each hold one value, are paired in order. This is IndiGo's "Booking Reference / Status / … then the values".
3. **Below it:** if the next row has no labels (a *value row*), each of its cells goes to the label whose **column** contains the cell's centre. Column boundaries lie halfway between neighbouring labels, so values may be wider or start further left than their header (`1234567890@117` under `PNR:@131`). The row must be on the same page and close (within 2.5 line heights).
   - **Second line:** a *shorter* value row right under the first one is appended (Booking.com: "from 14:00" under the check-in date). A *full* row is the next record of a table, not a continuation, so passenger 2 is never glued onto passenger 1.

Then, for labels that do have a value:

4. **Wrapped values:** a short free cell right under a value (aligned with the value or its label) is appended, but **only in clear cases**: a date cut after the month (`14-Aug-` + `2027 18:05`) or a station whose code wrapped (`YESVANTPUR` + `JN(YPR)`).

### 4. Using the fields (`TextFacts`)
`LabelValue`, `DateTimeNear` and `PersonNames` look at **form fields first** and only then at lines. Every parser rule written before this change gets the better answer **without being rewritten**. Field labels are matched with the same regex fragments the parsers already use (`^from` still doesn't match "Booked From").

### 5. Provider rules (`TravelDocumentParser`)
- **Indian Railways** is detected from IRCTC / RailOne / "Electronic Reservation Slip" / quota / `CNF/…` status codes. Then:
  - **From = "Boarding At"** (where you actually get on), then "Booked From", then "From". **To = "Resv. Upto"**, then "To".
  - Station places get country IN and **Asia/Kolkata**, so reminders are right. The city is cleaned: "KSR BENGALURU" → Bengaluru, "PATNA JN" → Patna (noise words JN, CANTT, CENTRAL, KSR…, then the longest phrase that is a known city).
  - The train value "20824/AII PURI SF EXP" splits into number **20824** and name **AII PURI SF EXP** (new `Segment.ServiceName`).
  - Each **passenger row** (`1  NAME  34  M  …  CNF/S4/56/SU`) gives the name and the *current* status (the last status on the row, since the booking status can be stale).
  - **`DescribeRailStatus`:**
    - `CNF/S4/56/SU` → seat "S4/56", "Confirmed · Coach S4 · Berth 56 · Side upper"
    - `GNWL/23` → "Waitlist 23"
    - `RAC/S2/7` → "RAC · Coach S2 · Berth 7"
    - chair-car coaches (C/D/E) say "Seat" instead of "Berth"
  - **Details:** Status, a line per passenger, Quota, Distance, Booked from (only if it differs), Transaction ID, Booked on.
- **Dates and times under separate labels** (`DateAndTime`): "Journey Date: 18/01/2027" + "Departure Time: 22:00". An **explicitly labelled time beats a time that only shares the date's line**: on an IndiGo boarding pass the first time after the date is the *boarding* time (13:55), not the departure (14:40). An arrival that prints only a time inherits the departure's date, plus one day if it is earlier ("dropping 06:30").
- **Flights:** if no airport codes are printed, labelled From/To city names are looked up in the airport list (Dibrugarh → DIB, Kolkata → CCU). Details: Status, Terminal, Gate, Boarding time, E-ticket.
- **Bus:** a route heading "Bengaluru → Hyderabad" is preferred over a boarding-point label whose value couldn't be placed. Details: bus type, boarding point (or its "Location"), landmark, reporting time, dropping point.
- **Hotels:** PIN, rooms, nights, room type, booked via (Booking.com when the hotel is the provider). Booking.com's dotted confirmation number `4128.553.912` is read as `4128553912`.

### 6. The ticket card (`DocumentPage`)
```
┌ 🚆 IRCTC                             [CONFIRMED] ┐  provider · status (colour + word)
│    20824 · AII PURI SF EXP                       │
│ AII               🚆                       PURI  │  codes 28 pt
│ Ajmer          ───────                     Puri  │
│ 19:00          43h 25m                    14:25  │  times 24 pt, travel time in the middle
│ Tue 31 Aug 2027                  Thu 2 Sep 2027  │
│ - - - - - - - - - - - - - - - - - - - - - - - -  │  perforation
│ PNR          COACH   BERTH   CLASS               │
│ 4521378965   S4      56      SL                  │
│──────────────────────────────────────────────────│
│ PASSENGERS                              Change   │
│ Anita Verma                                      │
│ Confirmed · Coach S4 · Berth 56 · Side upper     │
└──────────────────────────────────────────────────┘
[ View ticket ]  [ Show barcode ]
More details ▾   (booking ref, full class name, quota, distance, transaction id, notes)
Trip · Files ·  Edit / Share / Save to phone · Private · Delete
```
- The travel time is computed in UTC when both ends have a time zone (a flight Bengaluru → Dubai), otherwise from the printed local times.
- Stays show Check-in / Check-out with "3 nights" in the middle. Events show only the start.
- Low-confidence values stay **orange** (FR-D6), now on the strip too.
- Passenger statuses are matched to travelers by the **name as printed** (`DocumentPassenger.RawName` = `DocumentDetail.Label`).

## Key decisions
| Decision | Why | Rejected alternative |
|---|---|---|
| Read by **position** (boxes), not by text order | The relation "value is under the label" only exists in 2-D; text order of a PDF or OCR result is arbitrary for multi-column forms | Per-provider regexes on extracted text: break whenever a provider moves a column or the PDF library orders text differently |
| One reader for PDF, OCR and plain text | Same rules, same tests; plain text becomes a character grid | Separate code paths per source: three times the bugs |
| Form fields are **checked first by existing helpers** | All earlier rules improve at once; old fixtures keep passing (all 51 did, unchanged) | Rewriting every parser around a new API |
| Label dictionary + strong/weak split | Tickets use a small vocabulary; weak words must not split sentences | Treat every capitalised word before a colon as a label: too many false pairs ("To view…") |
| Column = halfway between neighbouring labels | Values are often wider than their header or start further left | "Value must overlap the label": fails on `1234567890@117` under `PNR:@131` |
| Only **clear** wraps are joined | Joining any short line under a value corrupts good values ("Distance: 1022KM" + "Passenger Details") | Always join aligned lines |
| Current status = last status on the passenger row | The booking status is what it was at booking; current is what the chart says | First status |
| `ServiceName` and `Details` as new **optional** fields | Aggregates are stored as JSON (C-003), so new fields need no migration and old phones ignore them | Squeezing the train name into `ServiceNumber`: breaks search and boarding-pass linking |
| Details labels in English | They mirror what is printed on the ticket; the app UI is English-only today | Translating labels at parse time |
| `ReadWordsAsync` as a **default interface method** | Test fakes and other recognisers keep working; only ML Kit overrides it | Changing `ReadTextAsync`'s signature everywhere |
| Ticket card with folded extras | People look for route, time, PNR and seat at a station; the old page was a long list | Tabs: one more tap for the most common need |

## Edge cases handled
- A PDF whose text layer is empty → OCR words keep their page number, so rows from page 1 and 2 never merge.
- Header row followed by a **table**: only the first record is paired with the headers, and the second row is not appended (it is a full row).
- "Departure Mumbai (BOM) Terminal 1 Sat, 12 Dec 2026 22:10": Departure takes "Mumbai (BOM)", Terminal takes "1", and the date stays text for the route rules.
- "Adult" in a table cell is not a label (removed from the dictionary; "Adult: 1" is still caught by the generic colon rule).
- "08Apr27", "Tue 16 Nov 27", "9:10 PM", "22:10 hrs", "14-Aug- 2027": all parsed.
- A 15-digit IRCTC transaction ID is kept whole (booking-code rules stop at 14 characters).
- Waitlisted tickets have no seat; the status says "Waitlist 9" in orange.
- Editing a segment keeps the train name unless the number was changed.

## Testing
- `FormReaderTests`:
  - value under a header;
  - several pairs on one line;
  - value after a gap;
  - colon-less runs;
  - stacked labels;
  - sentences rejected;
  - OCR boxes at slightly different heights.
- `ProviderLayoutTests`: eight provider layouts end to end, six rail status codes. **`Pdf_slip_is_read_from_word_positions` writes a PDF with PdfPig's `PdfDocumentBuilder`** at the 2023 slip's real column positions and runs the full `ContentExtractor` → `TravelDocumentParser` path. That is how the PDF path is tested without a real ticket.
- All earlier tests pass unchanged: 72 parsing tests, 288 in the solution.

### Emulator check (2026-11-18, Android 15 emulator, debug build)
A generated PDF with the 2023 slip layout (made-up values) was imported with **+ → Choose a file**. Everything was read on the phone:
- PNR, AII → PURI, 19:00 / 14:25 (43h 25m), train 20824 · AII PURI SF EXP, class SL;
- both passengers with Confirmed · Coach S4 · Berth 56 · Side upper / Berth 53 · Upper;
- quota, distance, booked on.

The card rendered as designed. Three findings, all dealt with:
- **"PURI" stayed in capitals.** A station whose name equals its code ("PURI (PURI)") was treated as a bare code with no city. Fixed: only a name *without* a bracketed code counts as a bare code, and a test now asserts the city "Puri".
- **The strip wrapped unevenly** (PNR + Coach, then Berth + Class). It is now a fixed two-per-row grid.
- **"Not responding" on a cold start** happened only while the emulator was out of memory: 2.4 of 2.5 GB used, 84 % kernel CPU from swapping, and another app on the same emulator also stopped responding. With 4 GB it did not happen. A share sent from `adb shell` doesn't reach the app, because the shell can't grant read access to a media file the way WhatsApp does. That is a test-harness limit; sharing from apps works on the phone.

## Interview Q&A
- **Why did plain-text parsing fail on the RailOne ticket?** The slip prints labels on one row and values on the next. A text extractor outputs "PNR: Train No./Name: Class:" then "1234567890 20824/… Sleeper Class", so "the text after PNR:" is another label. The relation is spatial, so the fix had to be spatial.
- **How do you decide which column a value belongs to?** Split the row at the midpoints between neighbouring labels and put each value cell where its centre falls. Edge columns extend by the label's width. It tolerates values that are wider or offset.
- **How do you avoid reading sentences as labels?** Weak labels need a colon, a cell of their own, or a run of labels. A phrase that merely starts with "to"/"from" is text, and more than six words after a colon-less label is a sentence.
- **How is it tested without real tickets?** Fixtures copy real layouts with made-up values. Plain text keeps its columns as a character grid. For the PDF path, the test *generates* a PDF with PdfPig and reads it back.
- **What happens with a provider you've never seen?** Unknown `Label:` cells are still paired, the generic rules run on the fields, and anything uncertain is marked Low so the review screen asks the user.
- **Why not use AI?** D34: free, offline and private. A layout-aware rules engine covers India's handful of ticket formats exactly, explains itself, and is unit-tested.

## Possible improvements
- A real RailOne screenshot or PDF in `samples/real` (git-ignored) to confirm the in-app layout. None was available online.
- Multi-leg tables (one row per flight under one header).
- Translating detail labels when the UI gets more languages.
