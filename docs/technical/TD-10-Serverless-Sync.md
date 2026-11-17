# TD-10 · Serverless Family Sync

## Summary
Family phones share one vault **without any server of ours**. Each phone keeps the full vault. Changes travel as **signed, encrypted, padded messages** through a free relay (ntfy.sh, or any self-hosted ntfy), which only ever sees ciphertext and keeps it for about 12 hours (files about 3 hours).

Conflicts are settled by **last-writer-wins on a Hybrid Logical Clock**. A phone that was away longer than the relay keeps messages asks the family to **republish state**, and any phone that has the data answers. Files move in **encrypted chunks** that are verified by SHA-256.

Membership has its own protocol:
- **In-person QR join**, confirmed by the inviting admin's phone;
- **remote join** with **admin approval after comparing a 6-digit safety code**;
- **removal and leaving** followed by **key and topic rotation**, so the departed phone gets nothing new.

The design builds on Use It Before's proven `NtfyRelay` and `SyncCodec`, with signatures, replay protection, key rotation and chunked files added.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Relay/IRelay.cs`, `NtfyRelay.cs` | Relay interface + ntfy client (copied from Use It Before, now behind an interface) |
| `Core.Sync/Wire.cs` | `Envelope`, `SignedChange`, message bodies, `WireCodec` (gzip → pad → AES-GCM → sign), batching |
| `Core.Sync/SyncState.cs` | `ISyncStore` (change log + outbox), `SyncOptions`, setting keys, invite/join records |
| `Core.Sync/FamilySync.cs` | The sync cycle, outbox writer (local changes), publishing, keys |
| `Core.Sync/FamilySync.Apply.cs` | Verifying and applying incoming changes (incl. membership rules) |
| `Core.Sync/FamilySync.Files.cs` | File requests, chunked sending/receiving, verification |
| `Core.Sync/FamilySync.Membership.cs` | Create family, invites, joins, approve/reject, remove, rotate, leave |
| `Storage.Sqlite/SqliteSyncStore.cs` + migration 2 | `change_log`, `outbox` tables |
| `tests/Core.Sync.Tests` | 15 tests: several complete phones through an in-memory relay that expires messages like ntfy.sh |

## How it works

### 1. The wire format
```
Envelope { v=2, familyId, from, seq, sent (HLC), kind, body }
   │ JSON → gzip
   │ pad: [4-byte length][data][zeros] up to a bucket of 384 / 768 / 1536 / 2600 bytes
   │ AES-256-GCM with HKDF(familyKey[epoch], "msg"), label "msg/<family>/<epoch>"
   │ ECDSA-P256 signature over the ciphertext with the sender's device key
   ▼
"tv2.<epoch>.<base64url ciphertext>.<base64url signature>"     (always ≤ 3,800 characters)
```
- The **epoch is readable** so the receiver knows which family key to try (old keys are kept). Everything else is encrypted.
- **Padding buckets** mean the relay can't tell a check-in from a document change by size (tested: two different messages produce the same length).
- Decompression is capped at 1 MB, against "zip bombs".

**Message kinds:** `checkin`, `changes`, `state-request`, `file-request`, `file-chunk` (with an attachment), `join`, `rekey`.

### 2. Signed changes
Every record change is a `SignedChange { type, id, familyId, version, author, payload (record JSON), signature }`. The signature covers all the other fields.
- The **outbox writer** (`HandleAsync`, a vault-event handler) turns each local change into a signed change, stores it in the **change log** (latest per record) and queues it in the **outbox**.
- When answering a state request, a phone **forwards the original signed changes**. A republisher can't alter or forge someone else's change, because the author's signature must verify.

### 3. The sync cycle (per family, about every 15 minutes, on app open, after a change)
```
away = last sync > 11 h ago (or never)
1. fetch new messages from the family topic (and old topics kept 3 days after a rotation)
   for each: pick key by epoch → decrypt → skip own → (join: special) → sender must be an ACTIVE member
             → signature must verify → seq must be higher than the last seen from that sender (replay check)
             → observe its HLC → handle by kind
2. lead admin: rotate keys if someone left
3. admin: read join requests on open remote-invite topics
4. answer state requests not already answered by another phone in this batch (one republish for all)
5. send files others asked for (each file at most once per 10 minutes, however many ask)
6. ask for files this phone is missing (each at most every 6 h)
7. if away or just admitted: publish a state request
8. flush the outbox (only once admitted)
9. check in every 6 h ("last seen")
```
Each step is wrapped. A network error in one step is recorded and the others still run. Unsent changes stay in the outbox, so nothing is lost while offline (tested: offline → outbox grows → back online → delivered).

### 4. Applying changes and resolving conflicts
A change is applied only if all of these hold:
- it is for this family;
- the author is known and their signature verifies;
- it wasn't written after the author was removed;
- it is newer than the change-log entry.

Then, per type:
- **Documents:** HLC last-writer-wins, plus `DocumentConflicts.Resolve`, which keeps local user edits against automatic changes (TD-03). Another phone can never change *my* private document.
- **Travelers, trips:** HLC last-writer-wins.
- **Family (membership):** the strictest rules:
  - written by an **active admin**, or by a member **leaving** where nothing but their own "removed" mark changes;
  - **key pinning:** an existing member's keys can never change, so a forged "same member, new key" record is refused;
  - a **new epoch** is accepted only when this phone already holds that epoch's key (delivered by `rekey`).

Applied changes are re-announced as `VaultChange(FromSync: true)`. Search and reminders update, but the outbox **doesn't** echo them back out.

### 5. The 12-hour relay window (D41)
ntfy.sh keeps messages about 12 h. A phone that hasn't synced for 11 h assumes it missed something and sends a **state request**. The first phone that sees it republishes its whole change log (packed into as few messages as fit). Phones that see someone else's answer in the same batch stay quiet. The answer is the same no matter which phone sends it, because it consists of the original signed changes. Tested: Dad's phone off for 2 days catches up the document **and** its file from Mom's phone.

### 6. Files
- **Sending:** the file key is HKDF(familyKey, "file/<fileVersionId>"). The file is split into chunks of ≤ 1.9 MB (ntfy.sh visitor attachments are ≤ 2 MB), and each chunk is AES-GCM sealed with label `chunk/<family>/<file>/<i>/<count>`, so chunks can't be reordered or swapped. Each chunk is published as an attachment with a signed `file-chunk` message (index, count, size, SHA-256 of the whole file).
- **Receiving:** encrypted chunks are kept in an `incoming` folder until all have arrived (possibly over several cycles). Then they are decrypted, concatenated, and the **size and SHA-256 are checked**, and the file is stored with a fresh local key (TD-02). Any mismatch discards the chunks; the phone asks again later.
- Phones proactively send **files they imported**. Anything else moves on request: automatic (missing files, at most every 6 h) or immediate when the user taps **View** (`IFileRequester`, D51).
- Files > 25 MB stay on the uploader's phone (a stated limit, not silent).

### 7. Membership protocols
**Create:** the creator is admin, with a random 256-bit family key (epoch 1) and a random topic (~190 bits).

**In-person QR (C-010):** the QR holds the family id, name, relay, topic, epoch, **family key**, the inviter's id and signing key, a **one-time invite id and secret**, and a 15-minute expiry.
1. The joiner saves a **provisional** family (version 0: only the inviter is known) and publishes a `join` with its public keys and an HMAC proof of the invite secret.
2. The inviting admin's phone checks: invite unused and not expired, proof valid, signature valid. It then adds the member and publishes the signed family record.
3. The joiner accepts it, since it's signed by the admin whose key came from the QR, the **trust anchor**. Now admitted, it sends a state request.

**Remote text invite (approved design point 1):** the text holds **no family key**: just the relay, a fresh join topic, a join secret and the admin's public keys.
1. The joiner sends its keys + HMAC proof, **sealed to the admin's agreement key** (ECIES), to the join topic.
2. Both phones compute the **6-digit safety code** from both public keys and the secret. The people compare it by phone call.
3. The admin taps Approve. The member is added, and the family snapshot + key are sealed to the joiner's device key and **signed by the admin**.
4. The joiner verifies the admin's signature (key from the invite) and opens the approval. Done.

Rejection is a signed "no". A forged request (wrong secret) never even appears for approval (tested).

**Remove (C-009):** the admin marks the member removed, then **rotates**: epoch+1, a new random key and a new topic. The new key is **sealed separately to each remaining member's device key** and announced in a signed `rekey` on the **old** topic, where everyone still listens. Members switch over. The removed phone can decrypt the announcement's envelope but finds no key for itself, sees itself removed in the signed family record, and stops syncing. Tested: after removal, a new document reaches Grandpa but not Dad.

**Leave:** the member publishes their own removal, then wipes the family's data and keys from their phone. The **lead admin** (lowest member id among active admins, so two admins never rotate at once) sees it and rotates automatically.

### 8. Privacy details
- **Private documents** are never published. If a document was shared and then made private, a **content-free tombstone** (id and version only) is sent, so other phones delete their copy (C-011, tested).
- The relay sees: topic name, timing, sizes (bucketed), IP addresses. It doesn't see content, names or which document changed.
- Files are only downloaded from the relay's own host (inherited from Use It Before's `NtfyRelay`).

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Relay (ntfy) + full copies on every phone | No server to run or pay for (D40); proven in Use It Before | Own backend (cost, operations); Google Drive (permission scopes/verification); pure P2P (phones rarely online at the same time) |
| Sign *and* encrypt | Encryption proves "someone with the family key"; signatures prove *which* member, which membership rules need | Encryption only |
| Signed changes forwarded verbatim | Any phone can answer a state request without being able to forge others' changes | Republisher re-signs (would let a member impersonate others) |
| Per-sender sequence numbers | Simple, effective replay protection on a topic anyone who learns its name could post to | Nonce caches (memory, expiry complexity) |
| Padding buckets | Hides change types and sizes from the relay | No padding (size leaks what happened) |
| HLC last-writer-wins + user-edit protection | Simple, deterministic, enough for family data (TD-01, TD-03) | CRDTs (complex), manual conflict UI (confusing for the persona) |
| Rotation on the old topic, keys sealed per member | Remaining members get the new key end-to-end; the removed device can't read it | Distributing the new key in person (impractical); a server (none) |
| Lead admin rotates on leave | Deterministic, no coordination needed | Every admin rotates (conflicting epochs) |
| Chunked, hash-verified files | Fits free relay limits; integrity is end-to-end | Base64 inside messages (4 KB limit); separate file host (cost) |
| In-memory relay for tests | 15 realistic multi-phone scenarios in ~13 s, including expiry | Testing against ntfy.sh (slow, flaky, rate limits) |

## Edge cases covered by tests
Join (both kinds), forged remote join, rejected join, documents and files reaching other phones, edits flowing back, deletes, removal with rotation (removed phone cut off), 2 days offline catch-up, private withdrawal, attacker messages (wrong key, junk), **replayed genuine message**, chunked file reassembly, view-on-demand after attachment expiry, leaving (wipe + automatic rotation), offline outbox.

## Interview Q&A
**Q: How do family members sync without a server?**
A: Each phone holds the whole vault. Changes are signed by the author's device key, encrypted with the family key, and posted to a free store-and-forward relay. The others poll it every 15 minutes or so. The relay only ever sees ciphertext and forgets it after about 12 hours. If a phone was away longer, it asks for a republish and any family phone answers with the original signed changes.

**Q: What stops someone who learns the topic name from injecting data?**
A: Messages must decrypt with the family key, be signed by a current member's device key, and carry a sequence number higher than the last one seen from that member. Junk, wrong-key messages and replays are all dropped silently (tested).

**Q: How do you remove someone from an end-to-end encrypted group?**
A: Key rotation. The admin generates a new key and a new topic and seals the key individually to each remaining member's public key. The announcement goes out on the old topic, so everyone can receive it, but only the remaining members can open their sealed key. The removed phone stays on the dead topic with the old key. Data it already had can't be recalled, and the UI says so honestly.

**Q: What happens if two phones edit the same document?**
A: Whole-record last-writer-wins by hybrid logical clock, which is deterministic on every phone. One protection: a field a person edited by hand isn't overwritten by an automatic change from another phone.

**Q: How do large files fit through a relay with a 2 MB limit?**
A: They're split into 1.9 MB chunks, each encrypted with a label binding it to its file and position. The receiver collects them over one or more cycles and verifies the whole file's SHA-256 before storing it.

**Q: How did you test this?**
A: With simulated phones, each a complete vault with its own encrypted database, keys and services, talking through an in-memory relay that expires messages and files exactly like ntfy.sh. A shared manual clock lets a test jump two days ahead.

## Possible improvements
- Self-hosted relay guide (longer retention).
- Selected-member sharing (approved for after the MVP): per-document keys sealed to chosen members.
- Compaction: answer state requests only with changes newer than the requester's last known version (requires a version vector).
