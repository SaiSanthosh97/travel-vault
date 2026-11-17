# TD-11 · Backup, Restore and First Launch

## Summary
`VaultBootstrapper` (Storage.Sqlite) handles four moments:
- **first launch:** master key, database, device keys and recovery code;
- **every launch:** unlock;
- **restore** from a backup;
- **erase everything.**

`BackupService` + `BackupFormat` (Core.Backup) produce **one encrypted file** containing the whole vault, readable only with the **recovery code** (D47). A restored phone continues as the **same family member** (C-012). `DeviceIdentityStore` keeps the device identity and recovery code in the encrypted settings, additionally wrapped by the key ring.

## Where the code is
| File | Purpose |
|---|---|
| `Storage.Sqlite/VaultBootstrapper.cs` | `CreateAsync`, `UnlockAsync`, `RestoreAsync`, `EraseEverything`; `OpenedVault` |
| `Core.Vault/DeviceIdentityStore.cs` | Member id, name, private keys, recovery code (each wrapped by the key ring) |
| `Core.Backup/BackupFormat.cs` | File format: zip with header, encrypted manifest, encrypted files |
| `Core.Backup/BackupService.cs` | Export (gather everything) and import (write into a new vault) |
| `tests/Core.Backup.Tests` | 4 tests |

## How it works

### First launch (`CreateAsync`)
1. Generate the **device master key** (DMK). The Android Keystore wraps it, and the wrapped form is written to `dmk.bin`, the only file outside the encrypted database.
2. Open/create `vault.db` with HKDF(DMK, "db").
3. Generate **device keys** (ECDSA + ECDH P-256) and a **member id**. Store them in settings, the private keys wrapped by the key ring.
4. Generate the **recovery code**, store it wrapped (so Settings can show it again after unlocking), and return it so the UI shows it once with "I've written it down".

### Every launch (`UnlockAsync`)
Read `dmk.bin` → unwrap with the Keystore → open the database → load the identity → `VaultSession`. If any step fails (the Keystore key is gone after a factory reset, or the file was copied to another phone), it returns `null`, and the app offers **Restore from backup** instead of crashing.

### Backup file format
```
backup.tvbackup (zip)
├── header.json            plain: { format: "travelvault-backup", version: 1, salt, iterations, createdAt }
├── manifest               AES-256-GCM(key, gzip(JSON manifest)), label "backup/manifest"
└── files/<fileVersionId>  AES-256-GCM(key, original file), label "backup/file/<id>"
key = PBKDF2-SHA256(recovery code, salt, 600 000 rounds)
```
The manifest contains the device identity (member id, name, private keys) and, per family: the family record, **all key epochs**, travelers, documents (incl. recently deleted), trips, the **signed change log**, this device's **message counter**, and whether it was removed from the family.
- The header is plain because the salt and round count are needed to derive the key. It contains nothing personal.
- Each entry has its own label, so entries can't be swapped between slots.
- Files are read and written **one at a time**, so a large vault doesn't need much memory.
- A wrong code fails at the manifest's GCM tag → `WrongRecoveryCodeException`. A non-backup file → `InvalidDataException` (tested).

### Restore (`RestoreAsync`)
1. Open the backup with the code (fail fast if wrong, **before** touching existing data).
2. Wipe the vault folder and create a **new DMK wrapped by this phone's Keystore**, then a new database.
3. Save the identity from the manifest: **the same member id and device keys** (C-012).
4. Import: re-protect every family key under the new key ring, then write the records, files (re-encrypted with fresh per-file keys), change log and search index.
5. Set the message counter to **backup value + 100,000**. Other phones reject sequence numbers they've already seen. If the old phone sent messages after the backup was made, the restored phone's next messages would be rejected as replays without this jump.

### Erase everything
`IKeyVault.Destroy()` deletes the Keystore key first (**crypto-shredding**: the DMK and therefore everything becomes unreadable instantly), then the files are deleted.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Backup = file the user stores anywhere | Free, no server; works with their own Drive, WhatsApp-to-self, PC | Our own cloud backup (cost, server); Android Auto Backup (disabled for security, D52) |
| Recovery code, not a password | 150 random bits can't be guessed; no weak passwords | User-chosen password (weak; would need Argon2-level stretching) |
| PBKDF2 600k on top of a strong code | Defence in depth if the code is partially leaked; built into .NET | No KDF (fine for random codes, but costs nothing to add) |
| Same member identity after restore | Family phones keep trusting the restored phone without a new invite | New identity (needs re-invite and approval) |
| Sequence-number jump | Avoids "replay" rejection of a restored phone's messages | Reset to 0 (messages silently dropped by family phones) |
| Signed change log in the backup | The restored phone can answer state requests for the family straight away | Rebuild it from sync (incomplete until everyone republishes) |
| Unlock returns null instead of throwing | A lost Keystore key is an expected situation, not a crash | Exceptions to the UI |

## Edge cases
- Wrong recovery code → clear error, nothing wiped (the code is checked before wiping).
- Backup from a newer app version → refused with a message.
- Absurd round counts in a crafted header are rejected (no denial of service).
- The DMK file copied to another phone → can't be unwrapped (tested with a different key vault).

## Interview Q&A
**Q: How can users recover their data if there's no server?**
A: Two ways. In a family, a new phone simply rejoins and syncs everything from the other phones. Solo users, or a family that lost all phones, use the encrypted backup file. It holds everything, including keys, and opens only with the recovery code shown at setup.

**Q: Why does the restored phone keep the old identity?**
A: Family members trust a device by its public keys. Restoring the same keys means the phone is still "Mom's phone" to everyone else, with no re-approval needed. The message counter jumps ahead so other phones don't treat its new messages as replays.

**Q: How do you make "erase everything" instant?**
A: By destroying the hardware-held wrapping key. Every byte of data is encrypted under keys derived from the master key, and the master key can only be unwrapped by that Keystore key. Once it's gone, the data is noise. Deleting the files afterwards is just tidying up.
