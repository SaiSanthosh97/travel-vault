# TD-12 · The Android App (MAUI) and Device Components

## Summary
`TravelVault.App` is a thin .NET MAUI app (MVVM with CommunityToolkit.Mvvm) on top of the tested components. It adds:
- **screens:** 17 pages;
- **the composition root:** `AppServices`, which builds every component the same way the tests do;
- **Android integration**, through five `Device.*` libraries:
  - **Vision:** ML Kit OCR and barcodes, PDF rendering;
  - **Security:** Keystore, biometric lock, FLAG_SECURE, brightness;
  - **Reminders:** alarms and notifications;
  - **BackgroundWork:** JobScheduler;
  - **Files:** share intake, Downloads.

All user-facing text (313 strings) lives in `Resources/Strings/AppResources.resx`, ready for translation (D27).

## Where the code is
| Path | Purpose |
|---|---|
| `src/Apps/TravelVault.App/MauiProgram.cs` | DI registration, reminder/background initialisation (also runs with no screen) |
| `App.xaml.cs` | Lifecycle: lock on return, sync while visible, import the share inbox after unlock |
| `AppShell.xaml` | Routes: start, welcome, recovery, 5 tabs (Home, Trips, Search, Family, Settings), detail routes |
| `Services/VaultHost.cs` | Create / unlock / restore / erase the vault; raises `Changed` for screens |
| `Services/AppServices.cs` | Composition root for an unlocked vault (same wiring as `tests/Core.Sync.Tests/Phone.cs`) |
| `Services/Background.cs` | `ReminderService` (plan → Android alarms), `SyncScheduler` (when sync runs) |
| `Services/LockService.cs` | App lock rules (timeout, Strict mode) |
| `Services/ImportCoordinator.cs` | Import from the UI and the share inbox; save directly or open the review screen |
| `Services/FileActions.cs` | View, Save to phone (with warning), Share (temporary copy, cleaned at start) |
| `Services/UpdateService.cs` | GitHub Releases update check + verified install (adapted from Use It Before) |
| `Services/Inbox.cs` | Shared files / notification taps that arrive before the app is unlocked |
| `Views/*.xaml`, `ViewModels/*.cs` | Screens; `VaultPage` base reloads on sync and sets screenshot protection |
| `Platforms/Android/MainActivity.cs` | Share target (`ACTION_SEND`, `SEND_MULTIPLE`, `VIEW`) for PDF, images, text |
| `Platforms/Android/AndroidManifest.xml` | `allowBackup=false`, no cleartext traffic, permissions with reasons |
| `Components/Device.*` | Android implementations of Core interfaces |

## How it works

### Start-up flow
```
StartPage ─ no vault ───────────────▶ Welcome (name → create, or Restore from backup)
          ─ key can't be unwrapped ─▶ Welcome?lost=1 (restore, or start over)
          ─ recovery code not yet confirmed ─▶ Recovery code (screenshots blocked)
          ─ app lock on ─▶ BiometricPrompt ─▶ Home tabs ─▶ process share inbox, offer update (daily, Wi-Fi)
```
`VaultHost.EnsureOpenAsync` also runs **without any screen**, when Android starts the app for the background job, a reboot or a time-zone change. The Keystore key isn't bound to user authentication, so sync and reminder re-planning work while the phone is locked (Security Model 7.2).

### Composition without a DI container for vault services
Core services need the unlocked session (member id, key ring), which doesn't exist at app start. So `AppServices` is built **by hand** when the vault opens, while MAUI's DI holds only long-lived app services (`VaultHost`, `LockService`, view models). That keeps the wiring explicit, identical to the tests, and easy to read in an interview.

### Sharing a ticket from WhatsApp (J3) end to end
1. Android starts `MainActivity` with `ACTION_SEND`. `SharedContent.ReadAsync` copies the bytes **immediately**, because the sender's URI permission is temporary. It accepts only `content://` URIs and caps size at 25 MB. The display name passes through `Path.GetFileName`, so another app can't inject a path.
2. Items wait in `Inbox`. Once the vault is unlocked, `ImportCoordinator` runs the import use case (TD-06):
   - already in the vault → a toast, then the existing document opens;
   - confident → saved, toast "Saved: Flight · Bangalore → New Delhi";
   - otherwise → **Review** screen: orange for uncertain values, a picker per printed name (existing traveler / new / ignore), and a merge switch for a likely duplicate.

### Screens and what they show
| Screen | Key behaviour |
|---|---|
| Home | Family switcher, sync status, **Next up** card (route, time, PNR, seat, travelers; *Open ticket*, *Boarding pass*), trip suggestions (make trip / not now), upcoming documents, ＋ (camera, photos, files, pasted text), pull to refresh |
| Trips / Trip | Upcoming / Ongoing / Completed, trip details, rename, delete (documents stay) |
| Search | Search-as-you-type (250 ms debounce) over FTS, type filter chips, all documents when empty |
| Document | Legs, details (orange = check), travelers (change), linked boarding passes and **who's missing one**, trip, file versions (*Use this*), View / Show barcode / Edit / Share / Save to phone / Private / Delete, combine duplicates |
| Viewer | PDF pages rendered on the phone, images, text. **Barcode mode** = full brightness + screen stays on. "Getting it from family…" when the file isn't here yet. |
| Edit | Every field incl. legs (an airport code fills city + time zone), identity number and expiry; edited fields become *UserEdited* |
| Family | Members + last seen, invite by QR (shown with screenshots blocked, expiry time) or by message, **join requests with the 6-digit safety code**, approve/reject, make admin, remove, rename, leave, other families, sync now |
| Join | Scan QR (photo + ML Kit) or paste invite. Remote join shows the code to read out and "Check if I've been approved". |
| Travelers | Profiles, names as printed (aliases), "This is me", delete |
| Settings | Name, app lock, lock timeout, Strict mode, key storage (StrongBox/TEE), reminder scope + fix permissions, backup to Downloads / share, show recovery code (re-authenticate first), restore, storage used, travelers, recently deleted, updates, about/licences, **Erase everything** (confirm + re-auth + type ERASE) |

### Device components (Android)
- **Device.Vision:** `MlKitTextRecognizer`, `MlKitBarcodeScanner` (PDF417/Aztec/QR/DataMatrix/Code128 mapped to Core formats), `AndroidPdfRasterizer` (OS `PdfRenderer`, white background, ≤ 2,200 px). ML Kit needs a file to read EXIF rotation, so bytes go to a private temp file that's deleted in `finally`.
- **Device.Security:**
  - `AndroidKeystoreVault`: AES-256-GCM key in the Keystore, **StrongBox first**, falling back to the TEE.
  - `AppLock`: BiometricPrompt with `BIOMETRIC_WEAK | DEVICE_CREDENTIAL`.
  - `ScreenSecurity`: FLAG_SECURE, barcode brightness + keep screen on.
- **Device.Reminders:**
  - `TravelReminders.ScheduleAll` makes the alarms match the plan exactly: each key becomes its own PendingIntent, and keys that are gone are cancelled.
  - The notification text lives **only inside the alarm intent**, never in a file. SharedPreferences hold just the keys.
  - Lock-screen visibility is **private**; the public version shows "Travel reminder".
  - `RescheduleReceiver` re-plans after boot, app update, clock or time-zone change.
- **Device.BackgroundWork:** copied from Use It Before. JobScheduler runs about every 15 min with a network constraint, persisted across reboots.
- **Device.Files:** `SharedContent` (share intake) and `Downloads.SaveAsync` (MediaStore on Android 10+; the public folder with a permission on 8–9).

### When sync runs (`SyncScheduler`)
- Every minute while the app is visible.
- 1.5 s after local changes (debounced; flush only).
- On pull-to-refresh and "Sync now".
- About every 15 min in the background (not in Strict mode).

After each run the reminder plan is recomputed.

### Security details in the UI
- **Screenshots are allowed** (C-017). FLAG_SECURE is applied on every screen only in Strict mode. Screens still mark themselves as sensitive (`VaultPage.Secure`), so per-screen blocking could be switched back on in one place (`Activity.SetSecure`).
- The invite QR contains the family key; its expiry time (15 min) is shown and the text asks people not to photograph or share it.
- "Save to phone" warns that Downloads is readable by other apps. Share copies live in the private cache and are deleted on the next start.
- Showing the recovery code and erasing require **re-authentication**, even when the app is already unlocked.
- `AppSettings` (SharedPreferences) holds only non-sensitive switches: the current family id, lock timing, toggles.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| MAUI + MVVM, same style as Use It Before (D1, D50) | One C# codebase, proven patterns, compiled bindings | Native Kotlin (not C#), Avalonia (less mobile glue) |
| Hand-built `AppServices` | Services depend on the unlocked session; identical to test wiring | Registering session-bound services in DI (lifetime and ordering problems) |
| Review screen only when needed | Most tickets save in one tap; uncertainty is shown, not hidden | Always review (slow), never review (silent errors) |
| System biometric prompt with device credential (C-013) | No new PIN to forget; the OS handles secure input | Custom PIN pad (needs its own storage and brute-force protection) |
| Reminder text only inside alarm intents | Nothing about trips is written outside the encrypted database | Saving reminder content in preferences |
| MediaStore for Save to phone | No storage permission on Android 10+ | Storage Access Framework picker (an extra step for elderly users) |
| Pinned AndroidX Biometric (C-015) | Build compatibility with MAUI 10 | Latest version (fails NU1608) |
| Strict mode without biometric-bound keys (C-014) | Reminders keep working | Auth-bound key (no reminders or background sync at all) |

## Build and size
- `dotnet build src/Apps/TravelVault.App -f net10.0-android` builds the debug app.
- `dotnet publish src/Apps/TravelVault.App -f net10.0-android -c Release` produces the release APK (**32.7 MB**, arm64), within the PRD's 40 MB target.
- A hand-installed debug APK needs `-p:EmbedAssembliesIntoApk=true`. Without it, .NET's Fast Deployment leaves the assemblies out of the APK, and the app exits at start ("No assemblies found"). This was discovered during the emulator smoke test.

## Emulator smoke test (2026-11-17, Android 15 emulator)
| Step | Result |
|---|---|
| First launch → welcome screen | ✅ renders in the Use It Before style |
| Enter name → Get started | ✅ vault created (Keystore, encrypted DB) |
| Recovery code screen | ✅ code shown (8 groups); **screenshot came out black: FLAG_SECURE works** (content checked through the accessibility tree) |
| Create family | ✅ "Rahul's family" |
| Share a ticket into the app (`ACTION_SEND`, text) | ✅ parsed: BLR → DEL, 12 Dec 2026 06:10, 6E 2345, PNR X7KQ2P, booking ref, IndiGo; review screen asked about the two new travelers |
| Save | ✅ toast "Saved: …"; **6 reminders scheduled**; document page shows legs, seat, PNR, travelers and "Boarding pass missing: Rahul Sharma, Priya Sharma" |
| Home | ✅ Next up card + upcoming list; **"Synced with family just now": reached the real ntfy.sh relay** |
| Issue found and fixed | The 👪 tab icon showed as an empty box (missing in the system emoji font), so it was changed to 👥 |

Not covered by the emulator (needs real phones): camera/QR scanning, biometric prompt (the emulator has no screen lock), a second phone joining. That is the two-phone manual test in 08-MVP-Scope 8.4. The same flows are covered by the automated multi-phone sync tests (TD-10).

## Interview Q&A
**Q: How does a PDF shared from WhatsApp get into your app?**
A: The main activity declares intent filters for `ACTION_SEND`/`SEND_MULTIPLE`/`VIEW` with PDF, image and text types. On receipt it copies the content URI's bytes straight away (the grant is temporary), queues them, and after unlock the import use case parses and saves them, or shows a review card.

**Q: How do reminders survive a reboot without a server?**
A: Alarms are cleared by Android on reboot. A boot receiver starts the app process headless, the vault opens using the non-auth-bound Keystore key, the planner recomputes everything from the local database, and the scheduler re-creates exactly those alarms by key.

**Q: How do you stop sensitive screens appearing in screenshots or the app switcher?**
A: `FLAG_SECURE` on the window while those pages are shown: passports, visas, private documents, the recovery code and the invite QR. In Strict mode it stays on everywhere.

**Q: Why did the debug APK crash on the emulator at first?**
A: .NET for Android's Fast Deployment keeps assemblies out of the APK and pushes them separately during an IDE debug session. Installing that APK by hand leaves the runtime with no assemblies. Building with `EmbedAssembliesIntoApk=true` (or a release build) fixes it. It's a deployment detail, not an app bug.

## Bug found after release: Settings crashed (C-021)
In v0.1.0, opening Settings closed the app. Reproduced on the emulator; the stack trace pointed at `AndroidKeystoreVault.IsHardwareBacked`, which converted Android Java objects (`IKey` → `ISecretKey`, `IKeySpec` → `KeyInfo`) with C# casts. Java objects coming through .NET for Android bindings must be converted with `JavaCast<T>()`, because a C# cast only checks the .NET wrapper type and throws `InvalidCastException`. The check now uses `JavaCast` and returns "don't know" on any failure, since it only drives an informational label.

A second, general fix: `ContentPage.OnAppearing` is `async void`, so any exception while a screen loads crashes the whole process. `VaultPage` now wraps every load in a try/catch that logs the error and shows "Something went wrong showing this screen", so one bad screen can never take the app down.

**Interview angle:** "How do you stop one screen from crashing a mobile app?" Guard the async-void boundary (lifecycle events and command handlers), log with enough context to reproduce the problem, and fix the root cause found in the stack trace.
