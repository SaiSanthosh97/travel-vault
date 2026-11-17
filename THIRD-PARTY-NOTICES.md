# Third-Party Notices

Everything Travel Vault uses is free. Checked at every release (D32).

## Shipped in the app

| Dependency | Used by | License |
|---|---|---|
| .NET runtime and libraries | All | MIT |
| .NET MAUI (Microsoft.Maui.Controls) | App | MIT |
| CommunityToolkit.Mvvm | App | MIT |
| QRCoder (invite QR codes) | App | MIT |
| Microsoft.Data.Sqlite.Core | Storage.Sqlite | MIT |
| SQLitePCLRaw (core, provider, `bundle_e_sqlite3mc`) | Storage.Sqlite | Apache-2.0 |
| SQLite3 Multiple Ciphers (native encrypted SQLite, inside `lib.e_sqlite3mc`) | Storage.Sqlite | MIT; SQLite itself is public domain |
| PdfPig (PDF text extraction) | Core.Parsing | Apache-2.0 |
| Open Sans font | App | SIL Open Font License 1.1 |
| Google ML Kit barcode scanning and text recognition (on-device models) | Device.Vision | Free to use under the [ML Kit Terms](https://developers.google.com/ml-kit/terms); closed source |
| Google Play services client libraries (used by ML Kit) | Device.Vision | [Android SDK License](https://developer.android.com/studio/terms) |
| AndroidX Biometric and other AndroidX bindings (Xamarin.AndroidX.*) | Device.Security, App | MIT (bindings), Apache-2.0 (AndroidX) |
| .NET bindings for ML Kit (Xamarin.Google.MLKit.*) | Device.Vision | MIT |
| Code adapted from *Use It Before* (NtfyRelay, SealedBox ideas, background work, updater, styles) | Several | MIT (same author) |

Vulnerability check (2026-11-17): `dotnet list package --vulnerable --include-transitive` reports no known vulnerable packages for Storage.Sqlite.

Pinned versions: `Xamarin.Google.MLKit.*` and `Xamarin.AndroidX.Biometric 1.1.0.30`. Newer versions need AndroidX libraries newer than .NET MAUI 10 supports and fail to restore (NU1608).

## Data

| Source | Used for | License |
|---|---|---|
| [mwgg/Airports](https://github.com/mwgg/Airports) | 7,918 IATA airports with city, country and time zone (`Core.Parsing/Data/airports.tsv`) | MIT, © 2014 mwgg |
| Airline designators, city aliases | Matching flight numbers, city search | Public facts, compiled for this project |

## Services

| Service | Used for | Terms |
|---|---|---|
| [ntfy.sh](https://ntfy.sh) (public instance of the open-source ntfy server) | Carrying end-to-end encrypted family changes and files between phones. Messages are kept ~12 h, files ~3 h. No account. The relay address can be changed to a self-hosted ntfy. | Free public service, used within its [terms](https://ntfy.sh/docs/privacy/) and limits |
| GitHub Releases | Distributing the APK and update checks | Free |

## Development and test only (not shipped)

| Dependency | License |
|---|---|
| xUnit | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
