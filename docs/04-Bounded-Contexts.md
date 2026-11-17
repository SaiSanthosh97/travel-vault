# 4. Bounded Contexts and Solution Structure

Follows the Use It Before model (D50): **each bounded context is a component library**. `Core.*` libraries are plain .NET (no MAUI), `Storage.*` handle persistence and `Device.*` hold Android code, with a thin MAUI app on top (MVVM). Namespaces start with `TravelVault.`.

## 4.1 Contexts
| Context | Component(s) | Owns | Reused from Use It Before |
|---|---|---|---|
| **Family & Membership** | `Core.Families` | Family, Member, Invite, roles, safety code, "last seen" | Adapted from `Core.Households` |
| **Travelers** | `Core.Travelers` | Traveler profiles, name normalisation and matching | — |
| **Vault** | `Core.Documents` | TravelDocument, Segment, FileVersion, visibility, merge | — |
| **Ingestion & Parsing** | `Core.Parsing` | Classifier, per-type parsers, BCBP decoder, PNR/date/time extractors, airport data | Ideas from `Core.DateExtraction`, `Core.LabelCapture` |
| **Duplicates** | `Core.Duplicates` | Exact/fuzzy detection, candidates | — |
| **Travel Planning** | `Core.Trips` | Trip, grouping suggestions, boarding-pass linking, status | — |
| **Search** | `Core.Search` (+ FTS in `Storage.Sqlite`) | Query parsing, ranking, index document model | — |
| **Reminders** | `Core.Reminders` + `Device.Reminders` | ReminderPlanner; Android alarms and notifications | `Device.Reminders` (TimedReminders, restore after reboot, battery checks) |
| **Sync & Relay** | `Core.Sync`, `Core.Relay` | Change envelopes, HLC merge, packing, file chunking, state/file requests; ntfy client | `Core.Sync` (SealedBox, SyncCodec), `Core.Relay` (NtfyRelay) |
| **Security & Keys** | `Core.Security` + `Device.Security` | Key hierarchy, file/field encryption, signing, recovery code; Keystore, biometric lock, FLAG_SECURE | New (SealedBox reused) |
| **Backup** | `Core.Backup` | Encrypted export/import format | Idea from `Core.Items` export |
| **Files & Vision** | `Device.Vision`, `Device.Files` | ML Kit OCR + barcodes, PDF rendering, share-intent intake, save to Downloads, temporary cache | `Device.Vision` (MlKitReaders) |
| **Background** | `Device.BackgroundWork` | Periodic sync job (~15 min) | Copied |
| **App update** | `Core.AppUpdater` | GitHub release check + SHA-256 verified download | Copied |

## 4.2 Context map
```
                    ┌──────────────┐
  Share intent ───▶ │  Ingestion & │──ParseResult──▶┌───────────┐
  Camera / file     │   Parsing    │                │   Vault   │──events──┬─▶ Search
                    └──────────────┘   ┌──────────▶ │ Documents │          ├─▶ Reminders
                          ▲            │            └───────────┘          ├─▶ Trips (suggest, link BP)
                    Travelers ◀────────┤                  ▲  │             └─▶ Duplicates
                    (matching)         │                  │  │ changes
                                       │              apply│  ▼
  Families ─────────────keys──────▶ Security ◀──── Sync & Relay ◀──▶ ntfy relay (internet)
     ▲                                  ▲                │
     └────────── member events ─────────┴────────────────┘
```
- **Ingestion → Vault**: customer/supplier. Ingestion produces a `ParseResult` and Vault decides what to store.
- **Vault → Search/Reminders/Trips/Duplicates**: published domain events (in-process).
- **Sync** is an anti-corruption layer. It translates aggregates ⇄ versioned wire records (`RecordEnvelope`), so the domain never sees JSON or relay concepts.
- **Security** is a shared kernel used by Sync, Storage, Backup and Families.

## 4.3 Solution layout
```
TravelVault.slnx
README.md · THIRD-PARTY-NOTICES.md · LICENSE (MIT) · .github/workflows/ci.yml
docs/                          ← these design documents
samples/                       ← synthetic + (later) redacted real tickets
src/
  Apps/TravelVault.App/        ← MAUI app: Views/, ViewModels/, Services/, Platforms/Android/, Resources/
  Components/
    Core.Families/   Core.Travelers/  Core.Documents/  Core.Parsing/
    Core.Duplicates/ Core.Trips/      Core.Search/     Core.Reminders/
    Core.Sync/       Core.Relay/      Core.Security/   Core.Backup/
    Core.AppUpdater/
    Storage.Sqlite/
    Device.Vision/   Device.Files/    Device.Reminders/
    Device.Security/ Device.BackgroundWork/
tests/
  Core.*.Tests/ (one per Core component) · Storage.Sqlite.Tests/
```

> **C-005 (2026-11-17):** the application services below are implemented in the plain .NET library `src/Components/Core.Vault` (ImportService, DocumentService, TravelerService, TripService, SearchService) so they are unit-testable. The MAUI app keeps only UI and device glue. **C-007:** trip membership is stored on documents (`TripId`).

## 4.4 App structure (feature-based inside the MAUI app)
```
Views/ & ViewModels/ grouped by feature:
  Home (Next up, Upcoming trips)   Documents (list, detail, viewer, review/edit)
  Import (share target, camera, picker)   Trips   Search   Travelers
  Family (members, invite, join, approve)   Settings (security, backup, storage, updates, relay)
Services/  ← application services (use cases) that coordinate components:
  ImportService, DocumentService, TripService, FamilyService, FamilySyncService,
  ReminderService, BackupService, LockService, UpdateService
```
Dependency rule: App → Services → Core/Storage/Device interfaces. `Core.*` never reference `Device.*` or MAUI. Device code is reached through interfaces defined in Core (`ITextReader`, `IBarcodeReader`, `IPdfRasterizer`, `IKeyVault`, `IReminderScheduler`).
