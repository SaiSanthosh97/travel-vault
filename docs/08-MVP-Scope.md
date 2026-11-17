# 8. MVP Scope

> **Implementation status (2026-11-17):** phases 1–6 are implemented. All Core/Storage components have unit and integration tests (230+ tests incl. 15 multi-phone sync scenarios), the Android app builds, and the release APK is 32.7 MB. Remaining before a first release: a two-phone manual test on real devices (8.4), a release signing key, and the GitHub repository + first release. See [technical/README](technical/README.md) for per-feature write-ups.

The MVP is the full first release I will implement after sign-off. "In" means built, tested and in the APK.

## 8.1 In the MVP
| Area | Included |
|---|---|
| Families | Create, multiple families + switcher, in-person QR invite, remote text invite with approval + safety code, roles, remove member with key/topic rotation, last-seen |
| Travelers | Profiles, aliases, matching, auto-create from tickets, no-name → uploader, reassign |
| Documents | All types in FR-D1 incl. Passport/ID, segments, file versions, Family/Private visibility, edit all fields, soft delete + "Recently deleted" |
| Import | Share sheet (single + multiple; PDF, images, text) from WhatsApp/Gmail/file managers; camera; image/file picker |
| Parsing | PDF text (PdfPig), PDF render + ML Kit OCR, ML Kit barcodes, IATA BCBP decoder, classifier, generic parsers for flight/train/bus/hotel/event/visa/passport/boarding pass, bundled airport list, confidence highlighting |
| Duplicates | Exact skip, strong-match merge prompt, weak-match hint, merge into versions |
| Trips | Auto-suggest + confirm/rename/merge/split, Upcoming/Ongoing/Completed, "Next up" card |
| Boarding passes | Auto-link to segment + passenger, grouped view, full-brightness barcode view |
| Notifications | 24 h / 3 h, missing boarding pass, trip tomorrow (my/all setting), visa/passport expiry, backup reminder; restored after reboot |
| Search | FTS across city/airport/passenger/provider/PNR/free text, with filters |
| Access | In-app viewer (PDF + images), fetch-on-view, Save to phone, Share |
| Sync | ntfy relay (configurable URL), signed + encrypted envelopes, HLC merge, chunked files, state/file requests, background job, outbox |
| Security | Everything in [07-Security-Model](07-Security-Model.md) incl. app lock, encrypted DB/files, FLAG_SECURE, no Android backup, strict mode |
| Backup | Encrypted export/import with recovery code |
| Updates | GitHub Releases check + verified download (from Use It Before) |
| UI | Material 3, Use It Before palette, light/dark, font scaling, TalkBack labels |
| Quality | xUnit tests per Core component + Storage; synthetic ticket fixtures in `samples/`; GitHub Actions CI building and testing (APK build job included) |

## 8.2 Explicitly NOT in the MVP
- **Sharing a document with selected members only** (D7). The MVP has Family/Private. Per-recipient sharing adds a separate key-wrapping path, so it comes right after the MVP ([09-Future-Scope](09-Future-Scope.md)).
- Gmail API import, forward-to-address, iOS/Windows, widgets, calendar export, flight status, translations, `.pkpass` import.

## 8.3 Build order (implementation phases)
1. **Foundation:** solution, components, CI, Security (keys, encryption), Storage (encrypted SQLite spike → schema), Families/Travelers/Documents domain + tests
2. **Import & parsing:** share target, camera/picker, Device.Vision, PDF, BCBP, parsers + fixtures, review screen
3. **Vault UX:** document list/detail/viewer, Save/Share, duplicates, search
4. **Trips & reminders:** grouping, boarding-pass linking, Next up, ReminderPlanner + Android alarms
5. **Sync:** relay, envelopes, HLC, chunked files, requests, background job, invites (QR + remote), removal/rotation
6. **Backup, settings, updater, polish**, accessibility pass, release APK build

## 8.4 Definition of done
- `dotnet build` and `dotnet test` pass on CI; the release APK builds
- Every FR in the MVP has at least one test (domain/parser/sync) or a documented manual check
- Two-phone manual test: create → invite (both kinds) → import → sync → offline view → remove member
- No paid dependency; every dependency recorded in THIRD-PARTY-NOTICES.md
