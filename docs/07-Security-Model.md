# 7. Security Model

> **Changed by C-020 (2026-11-17):** backup, restore and the recovery code were removed at the user's request. A new phone gets its data back by rejoining the family. Anything below about backups or the recovery code no longer applies.

> **Implemented (2026-11-17).** Refinements: C-009 (rotation after removal and after a self-leave), C-010 (one-time proof in QR invites), C-012 (restore keeps the member identity, sequence numbers jump ahead). Details: [technical/TD-02](technical/TD-02-Security-and-Keys.md), [TD-10](technical/TD-10-Serverless-Sync.md), [TD-11](technical/TD-11-Backup-and-First-Launch.md).

Goal: **more secure than Use It Before** (D52). Use It Before already has end-to-end encryption, but stores its family key and database unencrypted, has no app lock, and allows Android backup. Travel Vault closes all of those gaps.

## 7.1 Threat model
| # | Threat | Mitigation |
|---|---|---|
| T1 | Phone lost or stolen while locked | Android full-disk encryption + our encrypted DB/files with keys in Android Keystore (hardware-backed / StrongBox when available) |
| T2 | Phone unlocked but the app is opened by someone else | App lock (biometric or phone PIN) on start and after 60 s in background (configurable); FLAG_SECURE hides the app in the recents screen |
| T3 | Relay operator / network eavesdropper | AES-256-GCM end-to-end; HTTPS only; random topic; size padding. The relay sees only ciphertext, timing and IP. |
| T4 | Someone guesses or learns the topic and posts junk or replays | Signatures from known member keys; replay check by sequence number; invalid messages are dropped silently |
| T5 | Invite text intercepted (e.g. leaked from a WhatsApp chat) | Remote invites contain **no family key**, only a one-time join secret. Admin approval with a **safety code** comparison is required, and invites are single-use with a 24 h expiry. |
| T6 | Removed member keeps reading | Key + topic rotation on removal; new keys are sealed only to the remaining members. Already-synced data can't be taken back (the UI says so). |
| T7 | A member forges records as someone else | Every change is signed by its author's device key; republishers forward the original signature |
| T8 | Malicious file shared into the app | File type detected by magic bytes (not file name); size limits; PDFs rendered with Android's sandboxed `PdfRenderer`; no scripts executed; PdfPig only extracts text |
| T9 | Other apps on the phone read files | All files in app-private storage, encrypted. Decrypted temporary files only in app cache, deleted after use. FileProvider grants are one-off, read-only. |
| T10 | Cloud/device backup leaks data | `android:allowBackup="false"` plus data-extraction rules that exclude everything. Only the user's own encrypted export exists. |
| T11 | Screenshots / screen recording of a passport | **Changed by C-017:** screenshots are allowed by default (user decision); FLAG_SECURE on every screen only in Strict mode |
| T12 | Tampered app update | Signed APK; the updater verifies the SHA-256 published on GitHub (copied from Use It Before); the update is installed only after Android's own signature check |
| T13 | Data leaking into logs, crash reports or notifications | No analytics, no crash reporting service, no personal data in logs. Notifications show minimal text on the lock screen (`VISIBILITY_PRIVATE`). |
| T14 | Backup file stolen | Encrypted with a key derived from the recovery code (PBKDF2-SHA256, 600k iterations, random salt) + AES-256-GCM |

## 7.2 Key hierarchy
```
Android Keystore AES-256 key ("tv_master_wrap")  — non-exportable, hardware-backed when available
  └─ wraps → Device Master Key (DMK, 256-bit random, held in memory only while the app runs)
        ├─ DB key            = HKDF(DMK, "db")      → opens the encrypted SQLite database
        ├─ per-file keys     (random, stored wrapped by DMK in file_versions.file_key)
        ├─ device signing key (ECDSA P-256), agreement key (ECDH P-256) — private parts stored wrapped by DMK
        └─ family keys        (random 256-bit per family per epoch, stored wrapped by DMK)
                └─ relay message key = HKDF(familyKey, "msg"),  relay file key = HKDF(familyKey, "file"|fileVersionId)

Recovery code (generated once, 128 bits, shown as 8 groups of 4 Crockford-Base32 characters)
  └─ PBKDF2-SHA256(600k) → backup key → encrypts the backup file (which contains the DMK-free export of all keys + data)
```
- All cryptography uses **.NET built-ins** (`AesGcm`, `ECDsa`, `ECDiffieHellman`, `HKDF`, `Rfc2898DeriveBytes`, `RandomNumberGenerator`), with no third-party crypto libraries.
- **App lock vs background sync:** the Keystore key does *not* require user authentication, so background sync can work while the phone is locked. The app lock is a UI gate. This trade-off is accepted because sync must run in the background. An optional **"Strict mode"** setting binds the Keystore key to user authentication: sync then pauses until the user unlocks the app.

## 7.3 Membership protocol
**In-person QR invite** (fastest, secure because both people are physically present):
QR = `{familyId, name, relayServer, topic, epoch, familyKey, inviterMemberId, inviterSigningKey, expiresAt}`. The joiner stores the data and publishes a signed `join` message announcing its public keys. All phones show "Asha joined".

**Remote invite (text via WhatsApp/SMS):**
1. The admin creates an invite: `{familyId, name, relayServer, joinTopic(random, temporary), joinSecret, adminAgreementKey, expiresAt}`. **There is no family key in the text.**
2. The joiner publishes to the join topic: its public keys + name, sealed to the admin's agreement key with ECDH, and authenticated with the join secret.
3. Both phones show a **6-digit safety code** = hash(adminKey, joinerKey, joinSecret). They compare it by phone call or in person.
4. The admin taps *Approve* → the family key + topic are sealed to the joiner's agreement key → the joiner is in. The join topic is abandoned.
5. Expiry 24 h, single use, revocable.

**Removal / rotation:** the admin generates a new family key + topic, signs a `rekey` message, seals the new key to each remaining member, and publishes it on the old topic. Phones switch over. Old keys are kept read-only in `family_key_history` to open older backups.

## 7.4 Android platform hardening
- `allowBackup=false`, `fullBackupContent` / `dataExtractionRules` exclude all data
- `usesCleartextTraffic=false`; network security config limited to HTTPS; system trust store only
- Share target activity validates content URIs and copies streams immediately; there are no exported components beyond the share target, launcher and boot receiver
- `FileProvider` for sharing out: per-file, read-only, temporary grant
- Exact alarms through `SCHEDULE_EXACT_ALARM`/`USE_EXACT_ALARM`, as in Use It Before's `TimedReminders`
- R8/trimming enabled for release; release signing key kept outside the repo (never committed, D50)

## 7.5 Privacy
- No accounts, analytics, ads or trackers. The only network destinations are the relay and GitHub (update check, which can be turned off).
- Identity numbers (passport/visa) are masked in lists and shown in full only on the detail screen, behind FLAG_SECURE.
- Delete family / leave family wipes that family's data and keys from the phone.
- "Erase everything" in Settings destroys the Keystore key, which makes all local data unreadable instantly.
