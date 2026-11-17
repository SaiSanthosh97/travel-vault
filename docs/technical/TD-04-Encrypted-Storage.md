# TD-04 · Encrypted On-Device Storage

## Summary
Each phone keeps all of its families' data in **one SQLite database file encrypted as a whole** by SQLite3 Multiple Ciphers (MIT licence). Each aggregate is stored as **one JSON document plus a few indexed lookup columns**. A full-text search table (FTS5) lives inside the same encrypted file. Original documents are stored outside the database by `FileVault` (TD-02), and their per-file keys are kept here, wrapped.

## Where the code is
| File | Purpose |
|---|---|
| `Storage.Sqlite/VaultDatabase.cs` | Open with key, wrong-key detection, one serialised connection, transactions |
| `Storage.Sqlite/Migrations.cs` | Versioned schema (`PRAGMA user_version`) |
| `Storage.Sqlite/SqlHelpers.cs` | Small helpers: parameters, queries, JSON, sortable UTC text |
| `Storage.Sqlite/SqliteDocumentRepository.cs` | Documents + file-hash index |
| `Storage.Sqlite/SqliteFamilyRepository.cs` | Families, wrapped family keys, "delete everything for a family" |
| `Storage.Sqlite/SqliteTravelerRepository.cs` | Travelers |
| `Storage.Sqlite/SqliteKeyValueStores.cs` | Settings, per-file keys |
| `tests/Storage.Sqlite.Tests/VaultDatabaseTests.cs` | 7 tests incl. "file is encrypted on disk" |

## How it works

### Opening the database
1. `SQLitePCL.Batteries_V2.Init()` (once) loads the **e_sqlite3mc** native library: SQLite compiled with encryption, shipped for Windows, Linux, macOS, **Android** and iOS by the official SQLitePCLRaw project.
2. Open the connection, then immediately run `PRAGMA hexkey = '<64 hex chars>'` with the 32-byte key from `KeyRing.DatabaseKey()` (HKDF of the device master key).
3. Run `SELECT count(*) FROM sqlite_master`. With a wrong key, SQLite reports error 26 (`SQLITE_NOTADB`), which becomes a `WrongKeyException`.
4. Apply migrations, then set `foreign_keys=ON`, `journal_mode=WAL` (readers don't block the writer, and crashes are safer) and `secure_delete=ON` (deleted content is overwritten inside the file).

The default cipher of SQLite3 Multiple Ciphers is **ChaCha20-Poly1305** (authenticated encryption per page). Every page of the file, including indexes, the FTS index and the WAL journal, is encrypted. The test `Database_file_is_encrypted_on_disk` checks that neither the SQLite header nor any stored text appears in the raw bytes.

### Concurrency
A single `SqliteConnection` guarded by a `SemaphoreSlim(1,1)`: every operation goes through `UseAsync`/`InTransactionAsync`. A phone app does a few operations per second at most, so one connection is simple and avoids SQLite "database is locked" errors between connections.

### Schema (migration 1)
| Table | Columns (besides JSON) | Purpose |
|---|---|---|
| `families` | id, version, is_deleted | Family aggregate |
| `family_keys` | family_id, epoch, wrapped_key | Family keys per epoch (old ones kept to read old backups/messages) |
| `travelers` | id, family_id, version, is_deleted | Traveler aggregate |
| `documents` | id, family_id, type, visibility, pnr, booking_ref, trip_id, starts_utc, ends_utc, version, is_deleted, deleted_at | Document aggregate + lookup columns |
| `document_files` | file_version_id, document_id, family_id, sha256 | Exact-duplicate lookup by hash |
| `file_keys` | file_version_id, family_id, wrapped_key, stored_at | "This phone has the file" + its wrapped key |
| `trips` | id, family_id, version, is_deleted | Trip aggregate |
| `settings` | key, value | Settings + sync bookmarks |
| `search_index` (FTS5) | document_id, family_id, title, provider, refs, places, passengers, notes | Global search, prefix-enabled (`del*`), accent-insensitive |

Indexes: `(family_id, is_deleted, starts_utc)` for the timeline, `(family_id, pnr)` and `(family_id, booking_ref)` for duplicates and boarding-pass linking, `(family_id, sha256)` for exact duplicates.

### Saving a document
In **one transaction**: upsert the `documents` row (JSON + normalised PNR/ref + start/end UTC), then replace its `document_files` rows. The lookup columns are derived from the aggregate on every save, so they can never disagree with the JSON.

### Migrations
`PRAGMA user_version` stores the schema version. `Apply` runs each missing step in its own transaction and bumps the version. Rule: a released step is never edited; changes are new steps.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Whole-file encryption (SQLite3MC) | Protects everything incl. indexes, search index and journal; no app code changes per column; MIT licence; official Android build | Column-level encryption (indexes and search would leak or be impossible); SQLCipher via `sqlite-net-sqlcipher` (older SQLitePCLRaw line, community builds being phased out) |
| `Microsoft.Data.Sqlite` + SQL (no ORM) | Small APK, full control of SQL (FTS5, upserts, PRAGMAs), no reflection-heavy mapping | EF Core (heavy on Android, startup cost); sqlite-net (used by Use It Before, but limited SQL, and its encrypted variant is on the older stack) |
| **Aggregate = one JSON document + lookup columns** (change C-003) | The aggregate is the unit of consistency and sync; storing it whole means the stored form *is* the sync form; adding a field needs no migration; queries only need a few columns | Fully normalised tables (segments, passengers, files…), as in the first DB design. More joins and mapping code, and every new field needs a migration and sync mapping. |
| Raw hex key (no KDF) | The key is already 256 random bits from HKDF, so password stretching adds nothing | `PRAGMA key='password'` (adds a pointless slow KDF) |
| Single connection + semaphore | Simplicity, no lock contention errors | Connection pool (more complexity on a phone) |
| WAL + secure_delete | Crash safety and responsiveness; overwrite deleted data inside the file | Default rollback journal |

## Small details worth knowing
- **Sortable UTC text:** times are stored as `yyyy-MM-ddTHH:mm:ss.fffZ`, so `ORDER BY starts_utc` sorts chronologically as text.
- Booking codes are stored **normalised** (`X7KQ2P`), so lookups match regardless of spaces, dashes or case.
- `ListDeletedAsync` excludes merged duplicates (`mergedInto` set), since they aren't "deleted by the user" and shouldn't clutter Recently Deleted.
- `PurgeDeletedAsync` removes tombstones older than a cutoff (90 days in the sync design), together with their file rows and search entries.
- `DeleteEverythingAsync(familyId)` wipes one family's rows from every table in one transaction (leave/removed/delete family).
- `ON CONFLICT (id) DO UPDATE` (upsert) keeps saves idempotent: applying the same sync change twice is harmless.

## Edge cases
- Wrong key or corrupt file → `WrongKeyException` (the app offers restore from backup).
- App killed mid-save → the transaction rolls back; WAL keeps the file consistent.
- Two families on one phone → every query filters by `family_id`; tested with `FindByReferenceAsync("other-family", …)`.

## Testing (7 tests)
Encrypted bytes on disk; wrong key rejected and right key reopens; FTS5 available in the encrypted build; full document round trip incl. HLC version, time zones, confidence, identity info; hash lookup ignores deleted documents + purge; family + keys + travelers round trip and family wipe; settings and file keys.

## Interview Q&A
**Q: How is data protected at rest?**
A: Two layers. The SQLite file is encrypted page by page (ChaCha20-Poly1305) with a key derived from the device master key, which is wrapped by a hardware-backed Android Keystore key. Secrets inside the DB (family keys, file keys) are additionally wrapped by the key ring. Original files are AES-GCM encrypted with per-file keys.

**Q: Why store JSON in a relational database?**
A: In DDD, an aggregate is loaded and saved as a whole, so normalising its internals buys little. We index only what we query (family, dates, PNR, hash). The JSON is exactly what we sync, so there's one serialisation path. SQLite is still valuable for transactions, indexes, FTS and encryption.

**Q: How do you evolve the schema?**
A: Ordered, append-only migration steps tracked by `PRAGMA user_version`, each in a transaction. Since most data is JSON, adding fields needs no migration at all; System.Text.Json ignores unknown properties and defaults missing ones.

**Q: How do you know the encryption actually works?**
A: A test writes a document with a unique marker string, then reads the raw file bytes and asserts that neither the marker, the PNR nor the standard "SQLite format 3" header appears. Another test proves a different key can't open it.

## Possible improvements
- Source-generated JSON for speed and trimming.
- Periodic `VACUUM` (also re-encrypts freed pages) as a maintenance job.
