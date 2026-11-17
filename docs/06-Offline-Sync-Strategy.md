# 6. Offline Sync Strategy (no server)

> **Implemented (2026-11-17)** as described, with refinements C-009 (rotation separate from removal; lead admin rotates when someone leaves), C-010 (QR invites carry a one-time secret; the joiner is provisional until the admin confirms) and C-011 (making a shared document private sends a content-free tombstone). Full detail: [technical/TD-10](technical/TD-10-Serverless-Sync.md).

## 6.1 Principles
1. **The local database is the truth.** Every action writes locally first. Sync only spreads it.
2. **Every phone has everything** it is allowed to see (D29), so any phone can answer for any other.
3. **The relay is a temporary encrypted mailbox**, not storage. It sees only ciphertext, a random topic name and IP addresses.
4. Based on the proven Use It Before design (`FamilySyncService`, `SyncCodec`, `NtfyRelay`), extended for larger files, signatures and key rotation.

## 6.2 The relay (ntfy)
| Property | ntfy.sh (default) |
|---|---|
| Cost | Free, no account |
| Message size | ~4 KB → we keep messages ≤ 3,800 characters (`NtfyRelay.MaxMessageLength`) |
| Message retention | ~12 h |
| Attachment | ≤ 2 MB each, kept ~3 h |
| Limits | Per-IP daily request/attachment limits (exact values checked in the spike; we stay well below them with batching + backoff) |
| Alternative | Any self-hosted ntfy URL (Settings → Relay server), per family |

One topic per family: 32 random base62 characters (~190 bits), so it can't be guessed. A new topic is created on each key rotation.

## 6.3 Message envelope
```
Envelope (JSON, then sealed):
  v=1, familyId, epoch, from(memberId), seq (per-device, increasing), sentAt(HLC),
  kind, body
Wire text = "1." + base64( AES-256-GCM(familyKey[epoch], padded(Envelope), label = familyId|epoch) )
            + "." + base64( ECDSA-P256(signingKey[from], ciphertext) )
```
- **Padding** to fixed buckets (1 KB, 2 KB, 3.8 KB) hides the content length.
- **Receiver checks, in order:** decrypt OK → known, non-removed member → signature valid → `seq` > last seen from that member (replay) → apply.
- **Records inside bodies are signed by their author** (`SignedChange`). When another phone republishes state, it forwards the original signed change unchanged, so authorship can't be forged by a republisher.

## 6.4 Message kinds
| Kind | Purpose |
|---|---|
| `checkin` | "I'm here", updates last-seen. Sent at most every 6 h. |
| `changes` | Batch of `SignedChange` records (documents, segments, travelers, trips, file manifests, member list), packed by `SyncCodec` to fit the size limit |
| `state-request` | "I was away > 11 h, please republish everything since HLC X" |
| `file-chunk` | Encrypted file chunk as an ntfy attachment, with a text header `{fileVersionId, chunkIndex, chunkCount, sha256(chunk)}` |
| `file-request` | "I'm missing these file versions/chunks" |
| `join` / `join-approved` | Remote invite handshake (see Security) |
| `member-removed` + `rekey` | New family key + new topic, sealed individually to each remaining member's agreement key |

## 6.5 Sync cycle (every ~15 min by background job, on app open, after a local change, on pull-to-refresh)
```
for each family:
  1. if now - lastSync > 11h → publish state-request
  2. fetch messages since lastMessageId (poll)
  3. verify & apply each message (see 6.3), skipping already-seen relay IDs
  4. answer state-requests (republish change_log since requested HLC, with random 0–60 s jitter;
     skip if another member already answered, i.e. we saw its republish)
  5. answer file-requests for files we hold (rate-limited: same file at most every 10 min)
  6. request missing files (at most every 6 h per file, or immediately on "View")
  7. flush the outbox (publish pending changes); exponential backoff on HTTP errors / rate limits
  8. update lastSync, lastMessageId
```

## 6.6 Conflict resolution
- **Per-record last-writer-wins** using a **Hybrid Logical Clock** (wall time + counter + memberId as the tie-break). This avoids problems from phones with wrong clocks.
- **Deletes** are tombstones (`is_deleted` + version), kept 90 days and then purged.
- **Field-level protection:** a `UserEdited` field beats a parsed value even when the parsed change is newer (e.g. a merge on another phone doesn't undo a manual correction).
- **Merges** (duplicates) are expressed as: B → tombstone with `mergedInto = A`, plus A's updated file list. Applying them is idempotent on every phone.
- **Membership changes** are accepted only if signed by a current admin.

## 6.7 Files
1. Each original file has a random **file key**. Locally it is encrypted with the file key (wrapped by the DMK).
2. **Over the relay:** the plaintext is re-encrypted with a key derived from the family key and the fileVersionId, then split into **chunks of ≤ 1.9 MB** (under the 2 MB limit after overhead). Each chunk is one ntfy attachment.
3. The file manifest (sha256, size, chunk count) travels inside the document's `changes`, so receivers know what to expect.
4. Receivers download the chunks (only from the relay's own host, as in Use It Before), verify each chunk hash and then the whole-file SHA-256, then store the file encrypted with a fresh local file key.
5. Attachments expire after ~3 h, so a phone that missed them sends `file-request` and **any** phone holding the file re-sends it.
6. **View** (D51): if `local_state = Missing`, the app sends an immediate file-request and shows "Getting it from family…". Once the file arrives, it opens and stays on the phone.
7. Large file guard: files > 25 MB are refused for sync with a clear message (they stay local). Images > 4 MB are re-encoded for sync, with the original kept on the uploader's phone.

## 6.8 What "offline" means for the user
| Situation | Behaviour |
|---|---|
| No internet | Everything works. Changes queue in the outbox. A badge shows "Not synced yet (3)". |
| Back online | The outbox flushes automatically and others' changes arrive. |
| Away > 12 h | A state request is sent and any family phone fills the gap (J14) |
| Only holder offline | Others show the document's details, with the file marked "Waiting for <name>'s phone". Backups cover permanent loss. |
| Relay down / rate-limited | Backoff and retry; Settings shows the last error and lets the user switch the relay URL |

## 6.9 Battery and data
JobScheduler ~15 min with network constraint (`Device.BackgroundWork`). Polling only, no persistent connection. Each fetch is one small HTTPS request when nothing changed. File chunks are downloaded only on unmetered networks unless the user taps View, or departure is within 48 h.
