# Synthetic samples

Made-up ticket texts (as a PDF text layer or OCR would produce them) used by `tests/Core.Parsing.Tests`.
None of these are real bookings. `passport-mrz.txt` uses the ICAO 9303 specimen ("Anna Maria Eriksson", Utopia).

Real samples go in `../real/` (redacted, not committed: see `.gitignore`).

Provider layouts (C-022) copy the **layout** of public sample tickets with **made-up values**:
`irctc-ers-2023.txt` (2023+ IRCTC/RailOne slip: headers with values below), `irctc-ers-classic.txt` (several pairs per line),
`irctc-ers-wrapped.txt` (agent slip, wrapped values, waitlist), `indigo-boarding-pass-text.txt`, `indigo-itinerary.txt`,
`makemytrip-flight.txt`, `redbus-ticket.txt`, `bookingcom-hotel-columns.txt`. Columns matter: these files are read as a
character grid, so keep values aligned under their headers when editing them.
