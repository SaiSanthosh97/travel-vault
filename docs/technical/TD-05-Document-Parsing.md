# TD-05 · Reading Travel Documents Without AI

## Summary
`Core.Parsing` turns a PDF, photo, screenshot or shared text into structured travel details, **entirely on the phone and with no AI** (decision D34). It trusts sources in this order:
1. **Standard machine formats, decoded exactly:** the IATA boarding-pass barcode (BCBP) and the passport/visa MRZ (ICAO 9303, with check digits).
2. **Labelled values:** "PNR: X7KQ2P", "Check-in: 12 Dec".
3. **Patterns:** airport-code routes, dates near keywords, titled names.
4. **Fallbacks:** flagged *Low* so the review card highlights them.

Every field carries a confidence. 13 synthetic tickets (flight, connecting flight, US-style, boarding pass, two trains, bus, hotel, two events, visa, passport) parse correctly in tests.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Parsing/ContentExtractor.cs` | File → text + barcodes. `MediaTypes` (magic bytes), device interfaces `ITextRecognizer`, `IBarcodeScanner`, `IPdfRasterizer`, PDF text via PdfPig |
| `Core.Parsing/Bcbp.cs` | IATA boarding-pass barcode decoder |
| `Core.Parsing/Mrz.cs` | Passport/visa MRZ reader with check digits and OCR repair |
| `Core.Parsing/TextFacts.cs` | Text toolkit: dates+times, labelled values, booking codes, person names |
| `Core.Parsing/DocumentClassifier.cs` | Which type of document is this? |
| `Core.Parsing/TravelDocumentParser.cs` | Per-type parsing: flight, boarding pass, train/bus, hotel, event, visa/passport |
| `Core.Parsing/ReferenceData.cs` + `Data/*.tsv` | 7,918 IATA airports (city, country, IANA time zone), 92 airlines, city aliases, booking brands |
| `samples/synthetic/*.txt` | Test tickets |
| `tests/Core.Parsing.Tests` | 51 tests |

## How it works

### Step 1: Extraction (`ContentExtractor`)
- The **file type is detected from its first bytes** (`%PDF-`, JPEG `FF D8 FF`, the PNG signature, RIFF/WEBP, HEIC `ftyp`, otherwise valid UTF-8 → text), **never from the file name**. A renamed or malicious file can't pretend to be something else.
- **PDF:** take the **text layer** with PdfPig's `ContentOrderTextExtractor` (exact, fast, reading order). Also render up to 4 pages (Android `PdfRenderer`) and scan them for barcodes, because boarding-pass PDFs carry the BCBP as a PDF417 image. If the text layer has < 80 characters (a scanned PDF), OCR the rendered pages instead.
- **Image** (camera, screenshot, WhatsApp photo) → ML Kit OCR + ML Kit barcode scan, on the phone.
- **Text** (an SMS or email body shared to the app) → used directly.
- Broken or password-protected PDFs never stop an import. Text extraction returns "" and the user can still store the file and type the details.
- Files over 25 MB are refused (`MaxFileSize`).

### Step 2: Classification (`DocumentClassifier`)
- A BCBP barcode → **Boarding pass** (certain). An MRZ → **Passport** or **Visa** (certain).
- Otherwise each type sums **weighted keyword signals**, e.g. Train: `train` 4, `coach|berth` 2, `platform` 2, `railway` 3. A known booking brand adds 4 to its type (redBus → Bus, Booking.com → Hotel).
- Confidence: High if score ≥ 8 and ≥ 2× the runner-up; Medium if it simply wins; Low on a tie. A score < 3 → *Other* (needs review).

### Step 3: The text toolkit (`TextFacts`)
**Dates.** Four patterns, applied per line with an "already used" mask so one piece of text is never read twice:
| Pattern | Examples |
|---|---|
| ISO | `2026-12-20` |
| Day-month-name | `12 Dec 2026`, `12-Dec-26`, `15Jan2027`, `14 February 2027`, `12 Dec` |
| Month-name-day | `Mar 5, 2027`, `Dec 12` |
| Numeric | `18/01/2027`, `05.03.27` |

- Numeric dates are **day-first by default** (most of the world). If one part is > 12, the order is certain. Otherwise the date is flagged `Ambiguous` and the review card warns "day/month assumed".
- **Missing year** ("Sat, 12 Dec"): the next such date counting from 60 days before the import date, so a ticket imported in November for 12 Dec gets this year, and one for 1 Mar gets next year.
- **Times:** `06:10`, `6:10 PM`, `18.45`, `2130 hrs`, `9 am`. Each date is paired with the nearest unused time on the same line (after it first, then before it), or with a line that contains only a time right after the date line.
- `DateTimeNear(label)` looks at every line carrying the label and **prefers a date that has a time** ("Departure: 19:20 20-Dec" beats "Date of Journey: 20-Dec").

**Labelled values.** `LabelValue("pnr")` finds `PNR: X7KQ2P` on the same line, or on the next line if the label ends its own line, but only when the label *starts* that line. Without that rule, "The Grand Residency **Hotel**" would wrongly take the next line (the address) as the hotel name. That bug was found by a test and fixed.

**Booking codes.** `FirstCode` takes the first 5–14 character alphanumeric token containing a digit (or exactly 6 letters, the classic airline PNR) that isn't a known word ("CONFIRMED", "BOOKING"…).

**Names.** Names are found after name labels (Passenger, Traveller, Guest, Attendee, Ticket holder, Name:), in lists under a "Passengers" header (stopping at the first non-name line), and in lines starting with a title (Mr/Mrs/Ms/Dr/Shri/Smt). `CleanName` strips numbering, "(Adult)", ages, gender letters and seat columns, stops at digits or noise words ("Seat", "Flight", "sample", "the"…), and accepts 2–5 name words.

### Step 4: Per-type parsing (`TravelDocumentParser`)
**Flights.** Routes are found by five strategies in order, stopping at the first that finds anything:
1. `BLR → DEL` / `BLR-DEL` / `BLR to DEL` on one line;
2. bracketed codes `(BLR) … (DEL)` paired in reading order (repeats removed);
3. itinerary table rows with two codes **plus** a time or flight number on the same line (`EK 569  BLR …  DXB …  04:25`);
4. lines starting with a code followed by a time (`BLR 06:10` / `DEL 08:55`);
5. city names (`Atlanta to Seattle`) resolved through the airport list.

Every candidate code must exist in the airport list **and** not be in a stop-list of three-letter words that are also airport codes (PNR, THE, AND, FEE, TAX, INR, MON…). Flight numbers are only accepted with a **known airline designator** (6E, AI, EK, DL…), which avoids false hits like "T2 3". Dates are assigned in order: each leg takes the next unused date-time near its line as departure, and the next one within 30 h as arrival. **Time zones come from the airport**, so 06:10 in Bengaluru and 09:10 in Dubai are each converted correctly.

**Boarding pass.** BCBP gives name, PNR, route, carrier, flight, seat and class exactly. The barcode has no departure time, so the time is taken from a "Departure" label in the printed text when present.

**Train / bus.** From/To come from `From: … To: …` on one line, labels (Boarding at, Dropping point, Destination…) or an arrow route. Station codes in brackets are kept, e.g. `KSR BENGALURU (SBC)`. Also read: train number, coach/berth (`B4/32`), bus operator.

**Hotel.** The name comes from a label or the first line with hotel words. Check-in/out come from their labels. The city is the first address part that's a known city (postcodes stripped), which gives the time zone.

**Event.** Event name, venue, date/time, seat/row/section.

**Visa / passport.** MRZ when present (exact). Otherwise labelled numbers, "Valid from/until", and the visa country from "X VISA".

### Step 5: BCBP decoding in detail
A fixed-width format. The first leg's 60 mandatory characters are: format `M`, number of legs, name (20), e-ticket flag, PNR (7), from (3), to (3), carrier (3), flight (5), **day of year** (3), compartment, seat (4), check-in sequence (5), status, and a hex length of the leg's extra data. Later legs repeat the 37 characters from PNR on. The decoder walks the legs using those hex lengths.
- **Year inference:** the barcode stores only the day of the year (e.g. 326). The decoder tries the previous, current and next year and picks the date **nearest the import date**, so day 5 seen on 20 Dec is next January.

### Step 6: MRZ in detail
Two lines of 44 (passport TD3, visa MRV-A) or 36 (MRV-B) characters. Line 2 has the document number + check digit, nationality, birth date + check, sex, expiry + check.
- **Check digit:** weights 7-3-1 repeating; digits as is, A–Z = 10–35, `<` = 0, sum mod 10.
- **OCR repair:** in number and date fields, letters that look like digits (O→0, I/L→1, S→5, B→8, Z→2) are converted. In the document number, O→0 is accepted only if the check digit then passes.
- Two-digit years: expiry is always 20YY; a birth year later than this year becomes 19YY.
- The MRZ is only accepted if the number or expiry check digit passes, so random text with `<<<` isn't mistaken for one.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| No AI; rules + standards | Free and offline (D32, D34). The highest-value data (boarding passes, passports) is in standard formats that can be decoded *exactly* | Cloud LLM (cost, privacy, needs internet); on-device ML model (large APK, opaque mistakes) |
| Barcode/MRZ before text | Machine formats are exact; text is guesswork | Text first |
| Confidence per field | Lets the review card highlight only what needs checking; protects good data | Pass/fail per document |
| Type from magic bytes | Security (a disguised file can't pass as a PDF) and correctness (WhatsApp often renames files) | File extension / MIME type from the sender |
| Bundled airport list with time zones | Offline city names, city search and correct local→UTC conversion for reminders | Online lookup (needs internet); no time zones (wrong reminder times abroad) |
| Day-first numeric dates + ambiguity flag | Correct for most countries; flags the uncertain case honestly | Guessing silently; using the phone's locale (a ticket's format doesn't follow the reader's phone) |
| Known airline designators only | Removes false flight numbers from random "AB 123" text | Any two-character code |
| Airport stop-list | Many English words and currency codes are also airport codes (PNR is Pointe-Noire!) | Accept every valid code |
| Labels must start the line to borrow the next line | Fixes "…Residency Hotel" taking the address as the name | Always take the next line |
| Text toolkit separate from type parsers | The same date/name/label logic serves every type, and is unit-tested on plain strings | Regexes scattered per parser |

## Edge cases handled
- Overnight trains (arrival 2 days later), connecting flights, flights across time zones.
- Boarding pass with no readable barcode → read from text, still typed as a boarding pass.
- Screenshot with no name → empty passenger list, Low confidence → the uploader is assigned at save (TD-06).
- Glued airline titles `SHARMA/RAHULMR` (TD-03).
- A US date `03/25/2027` (month first, detected because 25 > 12).
- Text that isn't a travel document → *Other*, needs review.

## Testing
51 tests: BCBP (IATA example, two legs, year inference, rejects), MRZ (ICAO specimen, OCR repair, check digits), the TextFacts date/time table (9 formats), missing years, ambiguity, label values, name cleaning, 13 sample tickets end to end, media-type detection, and the airport directory.

## Interview Q&A
**Q: How do you parse tickets from hundreds of providers without AI?**
A: Most of the critical data follows standards: boarding passes carry an IATA BCBP barcode and passports an ICAO MRZ, both decoded exactly. For the rest, a reusable text toolkit finds dates with times, labelled values and names, and per-type rules assemble them. Each field gets a confidence, and the user confirms only the uncertain ones. When a format fails, we add it as a test fixture and fix the rule. That's a fast, transparent loop.

**Q: How do you get the year on a boarding pass that only stores the day of the year?**
A: Try the day in the previous, current and next year, and take the date nearest the import date. Passes are imported close to the flight, so this is right in practice, including around New Year.

**Q: How do you avoid false positives like "THE" or "PNR" being read as airports?**
A: Three filters: the code must exist in the bundled IATA list, must not be in a stop-list of common words and currency codes, and must appear in a route context (arrow, brackets, a table row with a time, or a line starting with a code and a time).

**Q: Why is 05/03/2027 treated as 5 March?**
A: Day-first is the most common convention worldwide. If either number is over 12 the order is certain. Otherwise we flag it as ambiguous so the review card asks the user to check, rather than silently guessing.

**Q: What's a check digit and why does it matter here?**
A: A digit computed from the field (weights 7-3-1, mod 10). If OCR misreads a character, the check usually fails, so we know the read is wrong and can try a repair (O→0) or lower the confidence, instead of storing a wrong passport number.

**Q: How do you make sure a fix for one ticket doesn't break another?**
A: Every format is a fixture file in `samples/synthetic` with an end-to-end test. The whole suite runs on every build, so a regression shows up immediately. Two real bugs (the "Hotel" label and "Synthetic sample." read as a name) were caught this way.

## Possible improvements
- More fixtures from real (redacted) tickets in `samples/real` (D31).
- `.pkpass` (Apple Wallet) import: a zip with JSON, so exact data (future scope).
- Phonetic name matching for transliterations.
