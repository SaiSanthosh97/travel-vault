# 5. Database Design (on-device SQLite)

> **Updated by C-002 / C-003 (2026-11-16).** The engine is **SQLite3 Multiple Ciphers** (`SQLitePCLRaw.bundle_e_sqlite3mc`, MIT) with `Microsoft.Data.Sqlite`. Each aggregate (family, traveler, document, trip) is stored as **one JSON record plus indexed lookup columns**, not as the normalised tables sketched in 5.2. The implemented schema is in `src/Components/Storage.Sqlite/Migrations.cs` and explained in [technical/TD-04](technical/TD-04-Encrypted-Storage.md). Sections 5.2–5.4 below are kept as the original logical model: the fields are the same, but segments, passengers and file versions live inside the document JSON.

There is no server database. Each phone has **one encrypted SQLite database** (`vault.db`) holding every family it belongs to, plus an **encrypted file store** (`files/`).

## 5.1 Storage engine
- **SQLite with full-database encryption** via **SQLite3 Multiple Ciphers** (MIT; `SQLite3MC.PCLRaw.bundle`) or SQLCipher community edition (BSD-style), whichever passes the licence + Android build spike. **Preferred: SQLite3MC**, because it has an MIT licence and works with `Microsoft.Data.Sqlite`.
- Fallback if neither builds on Android: plain SQLite + field-level AES-GCM on sensitive columns, with search via a local in-memory index (FTS can't index ciphertext).
- Access: `Microsoft.Data.Sqlite` + hand-written repositories (as in Use It Before's `Storage.Sqlite`), with no ORM to keep the APK small.
- Schema migrations: `PRAGMA user_version` + ordered migration scripts.
- Times are stored as UTC ISO-8601 text **plus** the original local time and zone where relevant.

## 5.2 Tables

### Identity and families
```sql
device_identity (            -- one row
  member_id TEXT PK, display_name TEXT,
  signing_private_key BLOB,   -- encrypted with the device master key (DMK)
  agreement_private_key BLOB, -- encrypted with DMK
  signing_public_key BLOB, agreement_public_key BLOB, created_at TEXT)

families (
  id TEXT PK, name TEXT, relay_server TEXT, relay_topic TEXT,
  key_epoch INTEGER, family_key BLOB,   -- encrypted with DMK
  my_role TEXT, created_at TEXT, last_message_id TEXT, last_sync_at TEXT,
  version TEXT, is_left INTEGER)

family_key_history (family_id, epoch, family_key BLOB /*DMK*/, retired_at)  -- for reading old messages/backups

members (
  family_id TEXT, member_id TEXT, display_name TEXT, role TEXT,
  signing_public_key BLOB, agreement_public_key BLOB,
  joined_at TEXT, removed_at TEXT, last_seen_at TEXT,
  last_seq INTEGER,                     -- replay protection
  version TEXT, PRIMARY KEY (family_id, member_id))

invites (id TEXT PK, family_id, kind, secret_hash BLOB, expires_at, status, used_by, created_at)
pending_joins (family_id, member_id, display_name, keys..., safety_code, received_at)  -- remote invites awaiting approval
```

### Travelers
```sql
travelers (id TEXT PK, family_id, display_name, linked_member_id, date_of_birth,
           version TEXT, is_deleted INTEGER, updated_at, updated_by)
traveler_aliases (traveler_id, normalized_name, raw_name, PRIMARY KEY (traveler_id, normalized_name))
```

### Documents
```sql
documents (
  id TEXT PK, family_id TEXT, type TEXT, title TEXT, visibility TEXT, owner_member_id TEXT,
  provider TEXT, booking_ref TEXT, pnr TEXT,
  identity_number TEXT, issuing_country TEXT, valid_from TEXT, expires_on TEXT,  -- visa/passport
  bp_target_segment_id TEXT, bp_traveler_id TEXT,                                -- boarding pass link
  trip_id TEXT, field_confidence TEXT /*JSON*/, notes TEXT,
  created_at, created_by, updated_at, updated_by, version TEXT /*HLC*/,
  is_deleted INTEGER, deleted_at TEXT)
  INDEX (family_id, is_deleted), INDEX (family_id, pnr), INDEX (family_id, booking_ref), INDEX (trip_id)

segments (
  id TEXT PK, document_id TEXT, ordinal INTEGER, mode TEXT,
  from_code, from_name, from_city, from_country, from_tz,
  to_code, to_name, to_city, to_country, to_tz,
  departs_local TEXT, departs_utc TEXT, arrives_local TEXT, arrives_utc TEXT,
  carrier_code, service_number, seat, travel_class)
  INDEX (departs_utc)

document_passengers (document_id, traveler_id, raw_name, match_confidence, seat,
                     PRIMARY KEY (document_id, traveler_id))
segment_passengers (segment_id, traveler_id, PRIMARY KEY (segment_id, traveler_id))

file_versions (
  id TEXT PK, document_id TEXT, sha256 BLOB, size INTEGER, media_type TEXT, original_name TEXT,
  is_primary INTEGER, added_at, added_by,
  file_key BLOB,          -- random per-file key, encrypted with DMK
  local_state TEXT,       -- Present | Missing | Downloading
  chunk_count INTEGER, version TEXT)
  UNIQUE (document_id, sha256), INDEX (sha256)       -- exact duplicate lookup
```

### Trips and suggestions
```sql
trips (id TEXT PK, family_id, name, starts_utc, ends_utc, ends_override_utc, confirmed INTEGER, notes,
       version TEXT, is_deleted INTEGER, updated_at, updated_by)
trip_travelers (trip_id, traveler_id)
trip_suggestions (id, family_id, document_ids TEXT /*JSON*/, name, status /*Open|Dismissed*/, created_at)  -- local only
duplicate_candidates (doc_a, doc_b, strength, reason, status, created_at)                          -- local only
```

### Search (FTS5)
```sql
CREATE VIRTUAL TABLE search_index USING fts5(
  document_id UNINDEXED, family_id UNINDEXED,
  title, provider, refs /*pnr, booking ref, service numbers*/,
  places /*codes + names + cities (from the airport list)*/, passengers /*names + aliases*/, notes,
  tokenize = 'unicode61 remove_diacritics 2', prefix = '2 3');
```
It is maintained by the Search context on domain events. Because the whole DB is encrypted, the index is encrypted too.

### Reminders (local only)
```sql
reminders (id TEXT PK, family_id, kind, target_id, traveler_id, fire_at_utc, title, body,
           state /*Scheduled|Shown|Dismissed*/, android_request_code INTEGER)
```

### Sync bookkeeping (local only)
```sql
outbox (id INTEGER PK, family_id, kind, payload BLOB /*signed envelope*/, created_at, attempts, next_attempt_at)
inbox_seen (family_id, relay_message_id, received_at)      -- dedupe relay messages, pruned after 48 h
file_transfers (file_version_id, direction /*In|Out*/, chunks_done TEXT /*bitmap*/, chunk_urls TEXT, requested_at, state)
change_log (family_id, record_type, record_id, version TEXT, signed_change BLOB)  -- latest signed change per record, used to republish state
settings (key TEXT PK, value TEXT)
```

## 5.3 File store
```
<app-private>/files/<family_id>/<file_version_id>.bin    -- AES-256-GCM(file_key, original bytes)
<app-cache>/view/                                        -- temporary decrypted copies for viewing/sharing, wiped on app start and after use
```
Files never live outside app-private storage, except through the explicit **Save to phone** action (D51).

## 5.4 Synced vs local-only
| Synced to family | Local only |
|---|---|
| families (name, members, roles), travelers, documents (Family visibility), segments, passengers, file_versions metadata + file contents, trips | device_identity, keys, outbox/inbox, reminders, suggestions, duplicate candidates, search index, settings, **Private documents** |
