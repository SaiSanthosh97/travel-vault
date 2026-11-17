# 1. Product Requirements Document — Family Travel Vault

> **Changed by C-020 (2026-11-17):** backup, restore and the recovery code were removed at the user's request. A new phone gets its data back by rejoining the family. Anything below about backups or the recovery code no longer applies.

Status: **Draft for sign-off** · 2026-11-16 · Source of decisions: [Requirements.md](../Requirements.md) (D1–D52)

## 1.1 Vision
On travel day, any family member opens the app, even without internet, and the right ticket is on screen within seconds. The app is free forever, has no accounts and no server, and nobody outside the family can read the documents.

## 1.2 Problem
Tickets are scattered across WhatsApp groups, email inboxes and booking apps, often on someone else's phone. At the gate or station, people scroll through chats on a weak connection, find the wrong version of an e-ticket, or discover the boarding pass was never downloaded.

## 1.3 Personas
| Persona | Description | What they need |
|---|---|---|
| **Organizer** (e.g. the adult child who books everything) | Books for parents, kids and self | Fast import from WhatsApp/Gmail, automatic sorting, knowing what's missing |
| **Traveler** (e.g. an elderly parent) | Not tech-savvy; travels on tickets someone else booked | A large "Next up" card, the ticket opens offline, reminders |
| **Remote relative** | Lives elsewhere and wants to know when family is travelling | Sees upcoming family trips and gets a "trip starts tomorrow" notification |
| **Solo user** | Uses the app alone | Same features, plus backups so nothing is lost when changing phones |

## 1.4 Goals and success measures
| Goal | Measure |
|---|---|
| Find the right ticket fast | ≤ 10 s from app open to the ticket on screen, offline |
| Little typing | ≥ 80 % of key fields filled automatically for common global formats (IATA boarding passes, major airline/OTA/hotel e-tickets) |
| Nothing missing | Missing-boarding-pass reminder for every flight segment 24 h before departure |
| Zero cost | $0 to build, distribute and use (D32) |
| Private | Documents never leave a family phone unencrypted (D12, D40) |
| Light | APK ≤ 40 MB; background sync uses no noticeable battery |

## 1.5 Non-goals (for now)
- Booking, price comparison, or live flight status
- Any server, account, AI service or paid API
- iOS/Windows (future, see [09-Future-Scope](09-Future-Scope.md))
- Reading the user's mailbox automatically (Gmail API)

## 1.6 Functional requirements
IDs are referenced by the MVP scope and by tests.

### Family workspace (FR-F)
- **FR-F1** Create a family. A user can be in several families and switches between them (D8).
- **FR-F2** Invite members by **QR code (in person)** or **invite text (remote)**. Remote invites need admin approval + a safety code check (see [07-Security-Model](07-Security-Model.md)).
- **FR-F3** Roles: **Admin** (invite, remove, rename family) and **Member**. The creator is an admin; an admin can promote others.
- **FR-F4** Remove a member → the family key and relay topic rotate, and the removed phone stops receiving data.
- **FR-F5** Members list shows "last seen" (from sync check-ins), as in Use It Before.

### Travellers (FR-T)
- **FR-T1** Traveler profiles (name + aliases), optionally linked to a member (D28).
- **FR-T2** Names parsed from a ticket are matched to profiles (case/order/title-insensitive, e.g. "MR SHARMA/RAHUL" ↔ "Rahul Sharma"). An unmatched name offers "create traveler" or "link to existing".
- **FR-T3** **If no passenger name is found, the document is assigned to the uploader's traveler profile** (D34). Any family member can reassign it.

### Documents (FR-D)
- **FR-D1** Types: Flight ticket, Train ticket, Bus ticket, Hotel booking, Event ticket, Visa, **Passport/ID** (added, see 1.8), Boarding pass, Other.
- **FR-D2** A document has 1..n **segments** (legs) for multi-leg journeys, plus 1..n **files** (versions).
- **FR-D3** Fields: passenger(s), source, destination, departure date/time (local to the place + time zone), arrival, PNR, booking reference, provider, flight/train/bus number, seat, class, hotel check-in/out, event venue/time, visa/passport number (masked), expiry date.
- **FR-D4** Visibility: **Family** (default) or **Private** (stays on the owner's phone, never synced) (D48).
- **FR-D5** Every field can be edited by any member who can see the document; edits sync.
- **FR-D6** Low-confidence parsed fields are highlighted for review.
- **FR-D7** Soft delete with a 30-day "Recently deleted" area.

### Import (FR-I)
- **FR-I1** Android share sheet: receive single/multiple PDF, image and text shares from WhatsApp, Gmail, file managers and any app (D9).
- **FR-I2** In-app: camera capture (document photo), pick a screenshot/image, pick a file.
- **FR-I3** Shared plain text (e.g. an SMS or email body) is parsed as a document; the original text is kept as a `.txt` file.
- **FR-I4** Import runs fully offline.

### Parsing (FR-P)
- **FR-P1** On-device only, no AI (D34): PDF text layer → else page render + ML Kit OCR; ML Kit barcode scan (PDF417, Aztec, QR, Code128).
- **FR-P2** IATA BCBP decoding of boarding-pass barcodes (passenger, PNR, from/to, carrier, flight, date, seat).
- **FR-P3** Rule-based classifier picks the document type; type-specific parsers extract fields with a confidence score each.
- **FR-P4** Bundled airport list (IATA code → city, country, time zone) to normalise places and support city search.

### Duplicates (FR-X)
- **FR-X1** Identical file (same SHA-256 within a family) → skipped with a message "Already in vault: <title>" (D20).
- **FR-X2** Same type + same PNR/booking ref + overlapping passengers + same date, but a different file → "Looks like an updated version. Merge?" Merging keeps both files as versions, with the newest as primary.
- **FR-X3** Weak match (same route, date and passenger but no reference) → shown as a dismissible "possible duplicate".

### Trips (FR-R)
- **FR-R1** Trip = named group of documents with travellers, start and end.
- **FR-R2** Auto-suggest trips from chained segments and stays; the user confirms, renames, merges or splits (D19).
- **FR-R3** Lists: **Upcoming**, **Ongoing**, **Completed** (computed from times).
- **FR-R4** Home screen "Next up" card: the next segment to use, with a one-tap ticket view.

### Boarding passes (FR-B)
- **FR-B1** A boarding pass is linked automatically to the matching flight segment + passenger (PNR + flight number + date + name).
- **FR-B2** Ticket and boarding pass are shown together on the segment.
- **FR-B3** The barcode view sets screen brightness to max and keeps the screen on.

### Notifications (FR-N) — all local (D21, D38)
- **FR-N1** 24 h and 3 h before each departure/check-in (opens the ticket).
- **FR-N2** Missing boarding pass: a flight segment within 24 h without a boarding pass for each passenger.
- **FR-N3** Trip starts tomorrow, for family members' trips (per-user setting: my trips / all family trips).
- **FR-N4** Visa/passport expiry: 90, 30 and 7 days before.
- **FR-N5** Backup reminder (if no backup for 30 days).

### Search (FR-S)
- **FR-S1** One search box across all documents of the current family: city/airport name or code, passenger, provider, PNR/booking ref, free text.
- **FR-S2** Results are grouped by trip and work offline, in under 200 ms for 5,000 documents.

### Document access (FR-A)
- **FR-A1** **View** opens the encrypted in-app copy; if it's missing, it is fetched from family phones and then kept (D51).
- **FR-A2** **Save to phone** exports a readable copy to Downloads (with a warning).
- **FR-A3** **Share** the original file through the Android share sheet (temporary decrypted copy, deleted afterwards).

### Backup (FR-K)
- **FR-K1** Export one encrypted backup file; restore it with the recovery code (D47).

### Security (FR-SEC) — see [07-Security-Model](07-Security-Model.md)
App lock, encrypted DB and files, FLAG_SECURE, no Android cloud backup, signed and encrypted sync, key rotation.

## 1.7 Non-functional requirements
| Area | Requirement |
|---|---|
| Offline | Every feature except sync works with no network. Sync resumes automatically. |
| Cost | Only free/permissive-licence dependencies (MIT/Apache/BSD), no paid services (D32, D50) |
| Performance | Cold start ≤ 2 s on a mid-range phone; import + parse of a 2-page PDF ≤ 5 s |
| Battery | Background sync about every 15 min using JobScheduler (as in Use It Before), with backoff when there are no changes |
| Storage | Settings shows vault size per family; every family file is stored on every member's phone (D29) |
| Accessibility | Supports system font scaling to 200 %, TalkBack labels, status colours always with text |
| Platform | Android 8.0 (API 26)+, target latest SDK (D26) |
| Localisation | English; all strings in resources (D27) |

## 1.8 Assumptions challenged and product improvements
| Original assumption | Challenge | Change made |
|---|---|---|
| A boarding pass is a separate document | A boarding pass belongs to one passenger on one flight leg | Boarding passes are linked to a **segment + passenger**, so "missing" can be computed per person |
| A ticket = one journey | Connecting flights and return tickets have several legs | Documents have **segments**; trips group segments |
| A trip = a date range | Two family members may travel separately at the same time | Trips are suggested per **traveller group + route chain** |
| Visa is the only identity document | Passports expire too, and many countries require 6 months' validity | Added a **Passport/ID** type with expiry reminders (6-month validity check is future scope) |
| A cloud server is needed for family sharing | It costs money and must be run by someone | **No server**: encrypted peer-to-peer sync over a free relay (D40) |
| AI is needed for parsing | It costs money, and the key data (BCBP barcodes, PNRs) follows standard formats | Rules + barcodes + quick manual correction; no-name tickets default to the uploader |
| Duplicate = the same file | Airlines resend changed e-tickets with the same PNR | Exact match is skipped; same-PNR changes become **versions** |
| Notifications need a push service | Every phone already has all the data | All reminders are **scheduled locally**, work offline and cost nothing |
| Users need accounts | Accounts need a server | **No accounts**: device identity + invite QR (D44) |
