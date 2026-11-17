# Travel Vault

All your family's tickets, boarding passes, hotel bookings, visas and passports in one place: **on your phone, offline, end-to-end encrypted, free.**

- **Android first** (.NET 10 MAUI), Android 8.0+.
- **No server, no accounts, no AI, no cost.** Family phones sync through the free ntfy.sh relay, which only ever sees encrypted data.
- Share a PDF or screenshot from WhatsApp, Gmail or your files and the details are read on the phone: boarding-pass barcodes, passport MRZ, PDF text, OCR.
- Built from reusable components, the same model as *Use It Before*.

**Download:** [latest release](https://github.com/SaiSanthosh97/travel-vault/releases/latest) · Releasing updates: [docs/RELEASING.md](docs/RELEASING.md)

Requirements and decisions: [Requirements.md](Requirements.md) · Design: [docs/](docs/README.md) · Change log: [docs/CHANGES.md](docs/CHANGES.md) · Technical write-ups per feature: [docs/technical/](docs/technical/README.md)

## Components

| Component | What it does |
|---|---|
| [`Core.Common`](src/Components/Core.Common) | Clock, ids (UUIDv7), hybrid logical clock, shared JSON settings |
| [`Core.Security`](src/Components/Core.Security) | AES-256-GCM, HKDF, ECDSA/ECDH P-256, recipient boxes, key ring, file vault, recovery code, safety code |
| [`Core.Families`](src/Components/Core.Families) | Families, members, roles, invites (QR / text) |
| [`Core.Travelers`](src/Components/Core.Travelers) | Traveler profiles, name normalisation, passenger matching |
| [`Core.Documents`](src/Components/Core.Documents) | Travel documents, legs, file versions, merge, conflict rules |
| [`Core.Parsing`](src/Components/Core.Parsing) | Reading tickets without AI: BCBP barcodes, MRZ, PDF text, dates/names, per-type parsers, airport data |
| [`Core.Duplicates`](src/Components/Core.Duplicates) | Strong / weak duplicate detection |
| [`Core.Trips`](src/Components/Core.Trips) | Trips, suggestions, status, boarding-pass linking |
| [`Core.Search`](src/Components/Core.Search) | Search index entries, safe queries |
| [`Core.Reminders`](src/Components/Core.Reminders) | Offline reminder planning |
| [`Core.Vault`](src/Components/Core.Vault) | Application layer: import, documents, travelers, trips, search use cases |
| [`Core.Relay`](src/Components/Core.Relay) | ntfy relay client (from Use It Before) |
| [`Core.Sync`](src/Components/Core.Sync) | Serverless sync: signed encrypted messages, files, joins, key rotation |
| [`Core.Backup`](src/Components/Core.Backup) | Encrypted backup file and restore |
| [`Core.AppUpdater`](src/Components/Core.AppUpdater) | GitHub Releases update check (from Use It Before) |
| [`Storage.Sqlite`](src/Components/Storage.Sqlite) | Encrypted SQLite (SQLite3 Multiple Ciphers), repositories, FTS5, bootstrapper |
| [`Device.Vision`](src/Components/Device.Vision) | Android: ML Kit OCR + barcodes, PDF rendering |
| [`Device.Security`](src/Components/Device.Security) | Android: Keystore, biometric app lock, FLAG_SECURE |
| [`Device.Reminders`](src/Components/Device.Reminders) | Android: alarms and notifications |
| [`Device.BackgroundWork`](src/Components/Device.BackgroundWork) | Android: periodic background sync |
| [`Device.Files`](src/Components/Device.Files) | Android: share target intake, Save to Downloads |

`Core.*` and `Storage.*` are plain .NET and fully unit-tested on any PC; `Device.*` contain Android code behind Core interfaces. The app is [`src/Apps/TravelVault.App`](src/Apps/TravelVault.App).

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet test tests/Core.Common.Tests        # or any test project
dotnet test TravelVault.slnx               # everything (needs the Android workload for the app)
```

Android app (MAUI Android workload, JDK 17, Android SDK):

```sh
dotnet workload install maui-android
dotnet publish src/Apps/TravelVault.App -f net10.0-android -c Release
```

The APK is written to `src/Apps/TravelVault.App/bin/Release/net10.0-android/publish/`. To install a **debug** APK by hand (e.g. on an emulator), add `-p:EmbedAssembliesIntoApk=true`.
Release signing: put a `signing.props` in `%USERPROFILE%\.travelvault\` (never commit keys).

## Contributing rules

- Free only: dependencies must be MIT, Apache-2.0, BSD or similar, recorded in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); no paid services.
- Every change is recorded in [docs/CHANGES.md](docs/CHANGES.md); every feature has a technical write-up in [docs/technical/](docs/technical/README.md).
- Never commit secrets (signing keys, passwords) or real tickets (`samples/real/` is git-ignored).
- Parsing changes need a fixture in `samples/synthetic/` and a test.
