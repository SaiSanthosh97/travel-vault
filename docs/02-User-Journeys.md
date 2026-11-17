# 2. User Journeys

> **Changed by C-020 (2026-11-17):** backup, restore and the recovery code were removed at the user's request. A new phone gets its data back by rejoining the family. Anything below about backups or the recovery code no longer applies.

Each journey lists the steps and what the app does behind the scenes. "Organizer", "Traveler" and similar names refer to the personas in [01-PRD](01-PRD.md#13-personas).

## J1 First launch and create a family
1. Install the APK from GitHub Releases → open the app.
2. Welcome screen: "Your travel documents, on your phone. No account. Free." → enter **your name**.
3. Turn on the app lock (fingerprint or phone PIN), recommended and on by default.
4. **The recovery code is shown once**, with "I've written it down" (it can be viewed again later after unlocking).
5. Create a family ("Sharma Family") or **Join a family**.
6. Behind the scenes: device master key created in Android Keystore, device signing/agreement keys generated, a family key + random relay topic created, and the creator's own Traveler profile created.

## J2 Invite a family member
**A. In person (QR):** Admin taps *Invite* → *Show QR*. The new member taps *Join* → scans → enters their name → done. They see the family's documents as they sync in.

**B. Remote (text via WhatsApp):** Admin taps *Invite* → *Send invite text* → shares through WhatsApp. The relative installs the app, pastes or opens the invite, and enters their name. Both phones show the same **6-digit safety code**; the admin compares it on a phone call and taps *Approve*. The new member then receives the family key.
- In-person QR invites expire after 15 minutes; remote invites after 24 h (configurable 1 h–7 days) and are single-use. (C-004)

## J3 Share a ticket from WhatsApp (main flow)
1. In WhatsApp, long-press the PDF → Share → **Travel Vault**.
2. If more than one family exists, pick which one (the last-used one is preselected).
3. The app imports, parses and shows a result card: *"Flight · Bengaluru (BLR) → Delhi (DEL) · 12 Dec, 06:10 · IndiGo 6E-2345 · PNR X7KQ2P · Rahul Sharma, Priya Sharma"*.
4. Fields with low confidence are highlighted. The user taps *Save* (or fixes a field first).
5. Behind the scenes: SHA-256 exact-duplicate check → parse → traveller matching → fuzzy duplicate check → save (encrypted) → schedule reminders → suggest a trip → publish to the family through the relay.
6. The other family phones receive it within about 15 minutes (immediately if their app is open).

## J4 Paper ticket via camera
Home → **+** → *Camera* → ML Kit document-style capture → crop → parse (OCR + barcodes) → same review card as J3.

## J5 Ticket without a passenger name
A screenshot of an event ticket shows no name → the app assigns it to **the uploader** ("Added for you. Change?"). Later, the user's spouse opens it → *Travelers* → picks "Ananya" → saved and synced.

## J6 Duplicate and updated tickets
- Dad and Mom both share the same PDF from the family WhatsApp group → the second phone shows *"Already in vault"* and doesn't import it.
- The airline emails a changed e-ticket (new time, same PNR) → *"Looks like an updated version of Flight BLR→DEL (PNR X7KQ2P). Merge?"* → **Merge**: the old file is kept as version 1, the new file is primary, and the reminders are rescheduled.

## J7 Boarding pass arrives
After web check-in, the user shares the boarding-pass image → the BCBP barcode is decoded → linked to the flight segment + passenger → the segment shows "Ticket ✓ Boarding pass ✓". If any passenger on the flight has no boarding pass 24 h before departure, the app reminds: *"Boarding pass missing for Priya — BLR→DEL tomorrow 06:10"*.

## J8 Travel day (offline)
1. A notification 3 h before departure → tap → the ticket opens straight away (the app lock is checked first).
2. The home screen *Next up* card shows the segment, with buttons for the boarding pass and ticket.
3. *Show barcode* → full-screen, brightness at max, screen stays on.
4. Everything works in airplane mode.

## J9 Search
Type "del" → the results show flights to/from **Delhi (DEL)**, the hotel in New Delhi, and passengers named "Delia". Type "X7KQ2P" → the exact booking. Filter chips: Type, Traveler, Upcoming/Completed.

## J10 Trip suggestion
The vault has BLR→DEL (12 Dec), a hotel in Delhi (12–15 Dec) and DEL→BLR (15 Dec) → banner *"Group these 3 into a trip 'Delhi, Dec 12–15'?"* → **Confirm** / *Edit* / *Dismiss*. A confirmed trip appears under Upcoming → Ongoing → Completed as dates pass.

## J11 View, save and share the original
Open a document → **View** (in-app viewer of the encrypted copy; if it's missing, "Getting it from family…" and then it opens) → **Save to phone** (warning: "A readable copy will be saved to Downloads, where other apps can open it") → or **Share** (share sheet; the temporary copy is deleted afterwards).

## J12 New phone or lost phone
- **In a family:** install → *Join* → the admin re-invites (J2) → all data re-syncs from family phones → the admin removes the old device (rotating the key).
- **Solo, or the whole family lost their phones:** install → *Restore from backup* → pick the backup file (from Drive/WhatsApp/PC) → enter the recovery code → the vault is restored.

## J13 Remove a member
Admin → Members → *Remove* → confirm → the remaining phones get a new family key and topic (sealed to each remaining device) → the removed phone shows "You were removed from Sharma Family" and stops syncing. Data already on that phone cannot be taken back; the UI says so honestly.

## J14 Missed sync while away (12-hour relay window)
Grandpa's phone is off for 2 days → on return it notices its last sync was more than 11 h ago → sends a *state request* → any online family phone republishes the current state + file manifests → missing files are requested and received in chunks → the home screen shows "Up to date".
