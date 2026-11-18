You are a Senior Product Architect and Staff .NET Engineer.

Help me design an Android-first application called Family Travel Vault.

Problem:
Family members share travel tickets in WhatsApp groups, emails and various booking apps. When travel day arrives, users struggle to locate the correct ticket because files are scattered across multiple applications and accounts.

Goal:
Create a centralized family travel management application where all travel documents are stored, searchable and available offline.

Users:
Families with multiple members living in different locations.

Core Requirements:

1. Family Workspace
- Create family
- Invite members
- Shared access to travel documents

2. Travel Documents
- Flight tickets
- Train tickets
- Bus tickets
- Hotel bookings
- Event tickets
- Visa documents
- Boarding passes

3. Upload Methods
- Share from WhatsApp
- Share from Gmail
- Share from File Manager
- Camera capture
- Screenshot upload
- Direct file upload

4. Parsing Requirements
- Automatically extract travel details
- Passenger name
- Source
- Destination
- Travel date
- Travel time
- PNR
- Booking reference
- Travel provider

5. Duplicate Detection
- Detect duplicate tickets
- Suggest merge operations
- Prevent clutter

6. Offline First
- Entire application must work offline
- Local storage is primary
- Cloud synchronization is secondary

7. Search
- Global search across all documents
- Search by city
- Search by passenger
- Search by provider
- Search by PNR

8. Trip Management
- Upcoming Trips
- Ongoing Trips
- Completed Trips

9. Boarding Pass Management
- Boarding pass linked to trip
- Ticket and boarding pass grouped together

10. Notifications
- Travel reminders
- Missing boarding pass reminders
- Upcoming trip notifications

11. Document Access
- View original file
- Download original file
- Share original file

Technology Constraints:
- Prefer C#
- Android First
- Clean Architecture
- SOLID Principles
- DDD-inspired design
- Feature-based organization

Provide:
1. Product Requirements Document
2. User Journeys
3. Domain Design
4. Bounded Contexts
5. Database Design
6. Offline Sync Strategy
7. Security Model
8. MVP Scope
9. Future Scope
10. Risks and Mitigations

Do not generate code yet.
Act as a product architect.
Challenge assumptions and improve the product.
---

# Clarifications & Decisions Log

_This section is updated as questions are answered. Decisions here override anything above._

## Round 1 — Architecture (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D1 | Mobile UI framework | **.NET MAUI** (Android first; iOS possible later with minimal rework) |
| D2 | Cloud backend | ~~ASP.NET Core + PostgreSQL + Blob~~ → **superseded by D33** |
| D3 | Parsing strategy | ~~Hybrid on-device + cloud LLM~~ → **superseded by D34** |
| D4 | Delivery scope | **Design docs first (10 artifacts) → user sign-off → full MVP implementation** (app + backend + tests). Future scope is documented only. |

## Round 2 — Users, Identity & Privacy (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D5 | Authentication | ~~Google + Phone OTP~~ → **superseded by D35** |
| D6 | Target region / parsers | **Global / generic**: IATA BCBP boarding-pass barcodes, generic airline/rail/bus/hotel/event formats, major OTAs. No region-specific provider parsers in the MVP. |
| D7 | Document visibility | **Shared with the family by default**; any document can be marked **Private** (owner only) or **shared with selected members** |
| D8 | Family membership | **A user can belong to multiple families**; each document belongs to exactly one family; the UI has a workspace switcher |

## Round 3 — Ingestion, Security & Scale (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D9 | Gmail ingestion | **Android share sheet only** in the MVP (attachments/text). Gmail API auto-import and forward-to-address go in Future Scope. |
| D10 | App lock | **Biometric / PIN** app lock |
| D11 | Local encryption | **Encrypted local DB (SQLCipher) + AES-encrypted files**, keys in Android Keystore |
| D12 | Cloud encryption | **End-to-end encryption**: the server stores only ciphertext for documents and metadata |
| D13 | Screen protection | **FLAG_SECURE** (screenshot blocking) on sensitive documents (visa, passport, anything marked private) |
| D14 | Cloud AI parsing | ~~Via backend, opt-in~~ → **removed (D34)** |
| D15 | Scale / budget | **Personal / friends (<100 users)**, **zero cost** (see D32) |

### ✅ Conflict E2EE vs AI: moot, AI removed (D34)

## Round 4 — E2EE details, Invites, Trips (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D16 | E2EE vs AI | ~~Transient OCR text to LLM~~ → **moot, no AI (D34)**; nothing ever leaves the device in plaintext |
| D17 | Key recovery | **Re-share from the family + recovery code**: any family member's device can re-wrap the family key for a member's new device; a printable recovery code covers solo or whole-family loss. |
| D18 | Invitations | **Invite link + QR code**. The invitee signs in, then an **existing member's device approves and wraps the family key** for them (required for E2EE). |
| D19 | Trip grouping | **Auto-suggest, user confirms**: overlapping dates and chained routes (A→B, hotel at B, B→A) are suggested as one trip; the user can rename, merge or split. |

## Round 5 — Duplicates, Notifications, Hosting, LLM (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D20 | Duplicate handling | **Exact file-hash match → auto-skip** with a toast. **Fuzzy match** (same PNR/booking ref + passenger, different file, e.g. an updated e-ticket) → **suggest merge** (keep both as versions, newest primary) or keep separate. |
| D21 | Notifications (all are local and work offline) | **24h + 3h before departure** (deep link to the ticket); **missing boarding pass** for flights ~24h before departure; **trip starts tomorrow**, sent to all family members; **visa/passport expiry** reminders |
| D22 | Hosting | **Local/home server**; ~~docker-compose with Postgres + MinIO~~ → **runtime superseded by D33** |
| D23 | LLM provider | ~~Anthropic Claude~~ → **removed (D34)** |

## Round 6 — Connectivity, Identity, Platform, UI (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D24 | Remote access to home server | ~~Cloudflare Tunnel~~ (needs a paid domain) → **superseded by D36** |
| D25 | Identity provider | ~~Firebase Authentication~~ → **superseded by D35** |
| D26 | Minimum Android | **Android 8.0 (API 26)**; target the latest SDK |
| D27 | UI / Locale | **Material 3, light + dark themes, English only**; all strings in resource files, ready for translation |

## Round 7 — Travelers, Offline Files, Quality (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D28 | Passenger model | Each family keeps **Traveler profiles** (display name + aliases such as "MR RAMESH K"), **optionally linked to a member account**. Parsed passenger names are fuzzy-matched to profiles; a linked member gets their own reminders. |
| D29 | Offline file policy | **Every device downloads every family file** it is allowed to see (private docs only to permitted members). Fully offline; uses more storage, and Settings shows storage usage. |
| D30 | Testing / quality | **xUnit unit + integration tests** (domain, parsers with fixtures, sync, API with Testcontainers PostgreSQL) + a **GitHub Actions CI** workflow. No automated UI tests in the MVP. |
| D31 | Parser samples | **Both**: synthetic fixtures generated now (flight, train, bus, hotel, event, visa, IATA BCBP barcodes); the user adds redacted real samples to `samples/` later for tuning. |

## Round 8 — ZERO-COST & LIGHTWEIGHT constraint (2026-11-16)

> **Overriding constraint from the user:** the solution must be **lightweight** and **completely free of cost** to develop **and** for end users. No paid APIs, subscriptions, domains, SMS, or store fees. Every dependency must be free / open source.

| # | Topic | Decision |
|---|-------|----------|
| D32 | Cost policy | **$0 to build, run and use.** Every dependency must be free/OSS-licensed or have a free tier we will never exceed at <100 users. Anything that could incur a charge is excluded. |
| D33 | Backend runtime (supersedes D2, D22) | **Single lightweight ASP.NET Core (.NET 10) minimal-API service** + **SQLite** (server DB) + **local filesystem blob storage** (encrypted blobs). Runs as one Docker container *or* directly via `dotnet`. No PostgreSQL, no MinIO. Storage behind interfaces, so Postgres/S3 can be swapped in later. |
| D34 | Parsing (supersedes D3, D14, D16, D23) | **No AI at all.** Parsing is fully **on-device and rule-based**: free Google ML Kit OCR (text) + ML Kit barcode scanning (IATA BCBP boarding passes, QR codes) + PDF text-layer extraction + regex/heuristic parsers per document type. **Passenger rule:** use the passenger name(s) printed on the ticket and match them to Traveler profiles (D28). **If no name is found, the ticket is assigned to the user who uploaded it**, and **any family member can edit the details or reassign the traveler** afterwards. Each parsed field has a confidence flag, and low-confidence fields are highlighted for review. |
| D35 | Sign-in (supersedes D5, D25) | **Google Sign-In only, no Firebase.** Android Credential Manager / Google Identity Services gets a Google ID token; the API verifies it directly against Google's public keys and issues its own short-lived JWT + refresh token. Free (Google Cloud OAuth client, no billing). |
| D36 | Remote access (supersedes D24) | **Tailscale free plan** (≤100 devices). The home server joins the tailnet; each family phone installs Tailscale once. No open ports or paid domain. The app's server URL is configurable (e.g. the Tailscale MagicDNS name). |
| D37 | Distribution | **Signed APK on GitHub Releases** + an in-app update check (no Play Store fee). Compatible with Obtainium for auto-updates. |
| D38 | Notifications | **Local notifications only** (computed on each device from synced data, including family-wide ones). No FCM/push service is needed. |
| D39 | CI | **GitHub Actions** (free for public repos; 2,000 free minutes/month for private repos, plenty for this) |

### Free dependency checklist (to verify during design)
- .NET 10 / MAUI / ASP.NET Core: free (MIT)
- SQLite, sqlite-net / EF Core SQLite: free
- SQLCipher (via SQLitePCLRaw sqlcipher bundle): check that the license is free; fallback is app-level AES encryption of sensitive columns
- Google ML Kit (on-device text + barcode): free, offline
- PdfPig (PDF text extraction): free (Apache 2.0)
- Tailscale personal plan: free
- Google OAuth client: free
- GitHub + Actions + Releases: free

## Round 9 — NO SERVER: peer-to-peer sync through a free relay (2026-11-16)

> The user asked for **no server at all**. Reference implementation: the user's own app **Use It Before** (`E:\AI Projects\Tracking`), which syncs families with **SQLite on each phone + E2EE messages over the free public ntfy.sh relay**. Relevant code: `Core.Relay/NtfyRelay.cs`, `Core.Sync/SealedBox.cs` (AES-256-GCM), `Core.Sync/SyncCodec.cs`, `Core.Households` (invite QR/text), `Apps/.../Services/FamilySyncService.cs`.

| # | Topic | Decision |
|---|-------|----------|
| D40 | Architecture (supersedes D2, D22, D33, D36) | **No backend of our own.** Each phone holds the full family vault in local SQLite + encrypted files. Phones sync by publishing **E2EE messages and files to an unguessable ntfy topic per family** (free public `ntfy.sh` by default; the server URL is configurable so a self-hosted ntfy can be used). The relay only ever sees ciphertext. |
| D41 | Sync model | Change-based messages (per-record versions / last-writer-wins with tombstones), packed below ntfy's ~3,800-char message limit. A phone offline longer than the relay's ~12h retention sends a **state request**, and any online family phone republishes the current state. Missing files are fetched with **file requests**, as in Use It Before. |
| D42 | Document files over the relay | ntfy.sh limits: **≤2 MB per file, kept ~3h**. Files are encrypted, then **split into ≤2 MB chunks** when larger. Images are compressed before sending; the original file is kept on the uploader's phone. Re-requested automatically when a phone missed them. |
| D43 | Accepted trade-off | Sync only completes while at least one phone that has the data is online. There is no permanent cloud copy. Mitigated by every phone keeping everything (D29) and by a manual encrypted backup export. |

| D44 | Identity (supersedes D5, D25, D35) | **No accounts, no sign-in.** On first launch the user enters a display name, and the device generates its own member ID + keypair. **Joining a family = scanning the invite QR or pasting the invite text**, which carries the family ID, relay topic and family key. Anyone holding an invite can join, so invites expire and admins can remove members (removal rotates the family key). |
| D45 | Invites (supersedes D18) | The invite QR/text **directly carries the encrypted family key**. No approval round-trip is needed, because there is no server to broker one. |
| D46 | Code reuse | **Copy and adapt** the proven Use It Before components into this repo (NtfyRelay, SealedBox, SyncCodec, invites, ML Kit readers, reminders, GitHub release updater). The two apps evolve independently. |
| D47 | Backup & recovery (supersedes D17) | **Encrypted backup file + recovery code**: "Export backup" produces one encrypted file the user saves anywhere (their own Drive, WhatsApp to self, a PC). It restores on a new phone with the printed recovery code. Optional periodic backup reminder. Family members can also simply rejoin by invite and re-sync from other phones. |
| D48 | Private documents (refines D7) | With one shared family key, **Private** documents are **never published to the relay**: they stay on the owner's phone and in their backups. Sharing with selected members uses those members' device public keys (sealed per recipient). |
| D49 | Testing (refines D30) | No server, so no API/Testcontainers tests. Tests cover domain, parsers (fixtures), sync codec/merge logic and encryption, with the relay faked in tests. |

## Round 10 — Design model, security hardening, viewing (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D50 | Design model | **Follow Use It Before's model where sensible:** reusable component libraries (`Core.*` = plain .NET domain/logic, `Storage.*` = persistence, `Device.*` = Android platform code) + a thin MAUI app (MVVM: Views / ViewModels / Services), a `.slnx` solution, one test project per component, the same visual language (Material 3 teal palette, light/dark, status colours always paired with text), README component table, `THIRD-PARTY-NOTICES.md`, permissive licences only (MIT/Apache/BSD, no GPL), no secrets in the repo. Each component maps to a bounded context (Clean Architecture/DDD). |
| D51 | View ticket / local copy | **View** opens the copy kept encrypted inside the app; if it is missing, the app fetches it from family phones first, then keeps it for offline use. A separate **"Save to phone" (Downloads)** button exports a normal readable copy on request, with a warning. Temporary decrypted files used for viewing or sharing go in the app cache and are deleted afterwards. |
| D52 | Security hardening ("more safe and secure" than Use It Before) | **Local:** SQLite DB encrypted (SQLCipher, or field-level AES-GCM fallback) + every file AES-256-GCM encrypted, master key wrapped by Android Keystore; biometric/PIN app lock with auto-lock timeout; FLAG_SECURE on sensitive screens; **Android cloud/device backup disabled** (`allowBackup=false`), so the only backup is the user's own encrypted export; no logging of personal data. **Sync:** AES-256-GCM with the family key, plus a **per-device ECDSA P-256 signature** on every message (built into .NET, no extra dependency), so members cannot be impersonated; replay protection with per-device sequence numbers; label binding (family + record ID) as in SealedBox; message size padding to hide content length. **Membership:** invites expire (e.g. 24h) and are single-use where possible; a **safety code** on both phones confirms an invite wasn't intercepted; **removing a member rotates the family key and relay topic**. **Relay:** HTTPS only, files downloaded only from the relay's own host, a self-hosted ntfy option. **Supply chain:** the APK is signed; the updater verifies the SHA-256 published on GitHub (as in Use It Before). |

### Relay retention explained (for the record)
ntfy.sh keeps messages ~12h and files ~3h. A phone that is away longer sends a **state request**/**file request**, and **any** online family phone holding the data answers, not only the original uploader. Background sync runs about every 15 minutes. The remaining gap is when a document's only holder goes offline before anyone else has received it; in that case it waits until that phone returns, or the user restores from a backup.

## Environment (verified 2026-11-16)
- .NET SDK **10.0.400** with the **maui-android** workload → target **.NET 10 (LTS)**
- Docker 27.1, git 2.46, JDK 17 installed
- `ANDROID_HOME` is not set. The Android SDK location needs checking before the first device build.
- Python is not installed (not required)

## Round 11 — Sign-off (2026-11-16)

| # | Topic | Decision |
|---|-------|----------|
| D53 | Design sign-off | **All 10 design documents and all 5 sign-off points approved**: remote invites need approval + safety code (refines D45); "selected members" sharing comes after the MVP; Passport/ID type added; optional Strict mode; encrypted-DB library chosen by a build spike (SQLite3MC preferred). |
| D54 | Change management | Changes stay possible. **Every change** to requirements, design or code behaviour is recorded in [docs/CHANGES.md](docs/CHANGES.md) (what, why, which documents were updated). |
| D55 | Technical design docs | For **every implemented feature**, a technical design document goes in [docs/technical/](docs/technical/README.md), covering minor to major details: how it works, **why this approach**, alternatives rejected, trade-offs, edge cases, testing, and interview Q&A. |

## Round 13 — Daily use feedback (2026-11-18)

| # | Topic | Decision |
|---|-------|----------|
| D58 | Reading tickets (extends D34) | Tickets are read **by layout**: a value can be beside its label or under it. Supported layouts: IRCTC/RailOne (all three slip formats, passenger table, berth codes), IndiGo, Air India / Akasa (generic airline rules), MakeMyTrip / Goibibo / Cleartrip / ixigo (travel-site e-tickets), redBus, Booking.com / OYO. Still no AI and still on the phone. (C-022) |
| D59 | Document screen | The document screen is a **ticket card**: route, times, PNR, seat and passengers first; other details folded. (C-023) |

## Round 12 — After the first build (2026-11-17)

| # | Topic | Decision |
|---|-------|----------|
| D57 | Backup & recovery code (supersedes D17, D47) | **No backup and no recovery code.** A new or reset phone gets its data back by rejoining the family; nothing to write down. Accepted risk: a solo user, or a family that loses every phone, loses the data. Backup reminders removed too. (C-020) |
| D56 | Screenshots (supersedes D13) | **Screenshots are allowed everywhere.** FLAG_SECURE is applied only when the user turns on Strict mode (then on every screen). (C-017) |

## Status (2026-11-17)
MVP implemented: 21 components + the Android app, 235 automated tests passing, release APK 32.7 MB, smoke-tested on an Android 15 emulator (incl. a live sync to ntfy.sh). Changes since sign-off: [docs/CHANGES.md](docs/CHANGES.md) (C-001…C-016). Per-feature technical write-ups: [docs/technical/](docs/technical/README.md) (TD-01…TD-12).

**Remaining before the first release:** two-phone test on real devices (camera, QR join, fingerprint lock), a release signing key, and the GitHub repository + first release (the update checker expects `github.com/SaiSanthosh97/travel-vault`).
