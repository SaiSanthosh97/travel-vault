# TD-02 · Security and Key Management

## Summary
All protection in Travel Vault is built from **.NET's built-in cryptography**, with no third-party crypto library. There are five building blocks:
1. `AeadBox`: AES-256-GCM authenticated encryption with a purpose label.
2. `DeviceKeyPair`: ECDSA P-256 signatures and ECDH P-256 key agreement.
3. `RecipientBox`: encryption for one specific device (ECIES).
4. `KeyRing` + `IKeyVault`: the key hierarchy rooted in the Android Keystore.
5. `RecoveryCode` / `BackupKey` / `SafetyCode`: the human-facing parts.

`FileVault` uses them to store every document encrypted with its own key.

## Where the code is
| File | Purpose |
|---|---|
| `src/Components/Core.Security/AeadBox.cs` | `AeadBox` (AES-GCM), `KeyDerivation` (HKDF) |
| `src/Components/Core.Security/DeviceKeys.cs` | `DevicePublicKeys`, `DeviceKeyPair`, `Signatures`, `RecipientBox` |
| `src/Components/Core.Security/KeyRing.cs` | `IKeyVault`, `InMemoryKeyVault`, `KeyRing` |
| `src/Components/Core.Security/RecoveryCode.cs` | `RecoveryCode`, `BackupKey`, `SafetyCode` |
| `src/Components/Core.Security/FileVault.cs` | Encrypted file storage |
| `tests/Core.Security.Tests/CryptoTests.cs` | 21 tests |

## How it works

### 1. AeadBox (authenticated encryption)
Byte layout: `version(1) | nonce(12) | ciphertext | tag(16)`.
- A fresh random **12-byte nonce** per encryption (`RandomNumberGenerator.Fill`), so the same plaintext never produces the same output.
- The **16-byte GCM tag** authenticates the data: any change to a single bit makes `Open` return `null`.
- The **label** is passed as *associated data*. It is not encrypted, but it is authenticated. For example, a file is sealed with label `file/<family>/<fileId>`. If someone swaps two encrypted files on disk, opening fails because the label doesn't match. This prevents "cut-and-paste" attacks.
- The version byte allows a future format change without breaking old data.
- On failure `Open` returns `null` (it doesn't throw) and zeroes the partial plaintext buffer.

### 2. Key derivation (HKDF)
`KeyDerivation.Derive(key, "db")` uses HKDF-SHA256 with info `travelvault/<purpose>`. One master secret yields independent keys for different jobs (database, messages, files). Knowing one derived key reveals nothing about another or about the master key.

### 3. Device identity: ECDSA + ECDH on P-256
On first launch each phone creates:
- a **signing key** (ECDSA P-256), which signs every sync message so others know who wrote it;
- an **agreement key** (ECDH P-256), which lets others encrypt data only this phone can read.

Signatures use the **IEEE P1363** format (fixed 64 bytes `r||s`) rather than DER (variable 70–72 bytes): fixed size is easier to pack into size-limited relay messages. `Signatures.Verify` returns `false` (never throws) for a malformed key, so a hostile message can't crash the app.

### 4. RecipientBox (ECIES: encrypt to one device)
Used to hand over the family key when someone joins, and for key rotation.
1. Create a throw-away (ephemeral) P-256 key pair.
2. ECDH(ephemeral private, recipient public) → shared secret.
3. HKDF(secret, salt = SHA-256(ephemeral public ‖ recipient public)) → AES key. Binding both public keys into the salt prevents key-substitution tricks.
4. AeadBox-encrypt with label `recipient/<label>`.
5. Output: `len(2) | ephemeral public key | box`.

Only the holder of the recipient's private agreement key can repeat step 2. The ephemeral key gives each message fresh keys.

### 5. Key hierarchy
```
Android Keystore key (hardware-backed, never leaves the secure chip)
  └─ wraps → Device Master Key (DMK, 32 random bytes)              ← KeyRing
        ├─ DB key = HKDF(DMK, "db")                               ← opens the encrypted SQLite file
        ├─ Protect(secret, label): per-file keys, family keys, device private keys
```
- `IKeyVault` abstracts "the thing that wraps the DMK". On Android it's the Keystore (Device.Security, later phase); in tests it's `InMemoryKeyVault`.
- `KeyRing.Protect/Unprotect` encrypts secrets with the DMK, with labels like `ring/family/<id>/<epoch>`.
- **Erase everything** = `IKeyVault.Destroy()` deletes the Keystore key. The wrapped DMK becomes useless, and every database row and file becomes unreadable instantly, with no need to overwrite gigabytes.

### 6. FileVault
- Each file version gets its **own random key**. The ciphertext goes to `files/<family>/<fileVersion>.bin`, and the key is stored (wrapped by the DMK) in the database.
- Writes go to `*.tmp` and are then **atomically moved**, so a crash or power loss never leaves a half-written file under the real name.
- Ids come from other phones, so they're untrusted. `Safe(id)` allows only letters, digits, `-` and `_` (max 64 characters), which blocks `../` path traversal.

### 7. Recovery code and backup key
- 30 random Crockford-Base32 characters (150 bits of entropy) + 2 check characters, displayed `XXXX-XXXX-…` (8 groups).
- Crockford's alphabet removes I, L, O and U. Parsing maps O→0 and I/L→1 and ignores case, spaces and dashes, so a handwritten code still works.
- The two check characters (10 bits of a SHA-256) catch about 99.9 % of typos **before** running the slow key derivation.
- `BackupKey.Derive` = PBKDF2-SHA256, **600,000 iterations** (the OWASP 2023+ recommendation), with a random salt per backup.

### 8. Safety code
6 digits = SHA-256(admin key fingerprint ‖ joiner key fingerprint ‖ invite secret) mod 10⁶. Both phones compute it independently. If an attacker intercepted the invite and substituted their own key, the admin's phone would show a different number from the real joiner's phone.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| .NET built-in crypto only | No supply-chain risk, FIPS-validated platform implementations, free, small APK | libsodium/NSec (extra native dependency); BouncyCastle (large, more API misuse risk) |
| AES-256-GCM | Authenticated (detects tampering), hardware-accelerated on ARMv8 phones, standard | AES-CBC + HMAC (two primitives, easy to get wrong); ChaCha20-Poly1305 (fine too, but AES-GCM is universally available in .NET on Android) |
| P-256 curves | Built into .NET on every platform; Android Keystore supports P-256 | Ed25519/X25519 (not in .NET's built-in API) |
| Per-file random keys | Compromise of one key exposes one file; crypto-erase per file | One key for all files |
| Keystore wraps a DMK (instead of encrypting everything with a Keystore key directly) | Keystore operations are slow and limited; the DMK can be exported **into the encrypted backup** for restore on a new phone, while Keystore keys can't be exported | Encrypt each item with the Keystore key directly (slow, no backups) |
| PBKDF2 for backup key | Built in; the input already has 150 bits of entropy, so the KDF is defence in depth, not the main protection | Argon2 (needs a third-party library) |
| `Open` returns null instead of throwing | Bad input from the network is expected, not exceptional. Callers can't forget a try/catch. | Exceptions |

## Small details worth knowing
- `CryptographicOperations.ZeroMemory` wipes derived keys and secrets after use (in `RecipientBox`, `KeyRing.Dispose`).
- The `KeyRing` constructor is private. Instances come only from `CreateNew`, `Unlock` or `FromRaw`, so a key ring always has a valid 32-byte key.
- `KeyRing.Unlock` returns `null` (not an exception) if the Keystore key is gone. The app then shows "Restore from backup".
- `SafetyCode` uses 4 hash bytes mod 1,000,000. The tiny bias (2³² isn't a multiple of 10⁶) is irrelevant for a human comparison code.

## Edge cases
- Wrong label, wrong key, truncated or flipped bytes → `null` (tests: `Wrong_label_wrong_key_or_tampering_fails`).
- A malformed public key in a message → `Verify` returns false.
- Path tricks in ids → `ArgumentException` (test: `File_vault_rejects_path_tricks`).
- An encrypted file modified on disk → `ReadAsync` returns null (test: `File_vault_round_trips_and_detects_tampering`).

## Testing
21 tests in `CryptoTests.cs` cover round trips, randomness, tampering, wrong keys, labels, signatures, recipient boxes, key export/import, recovery-code parsing and typo detection, PBKDF2 determinism, safety-code sensitivity, key-ring unlock and destroy, and file-vault integrity and path safety.

## Interview Q&A
**Q: What is authenticated encryption and why do you need it?**
A: It gives confidentiality *and* integrity: the GCM tag proves the ciphertext wasn't modified. Without it, an attacker who can't read the data could still flip bits (e.g. in a relay message) and cause predictable changes after decryption.

**Q: What is the "label" / associated data for?**
A: It binds the ciphertext to its context. A file encrypted for `file/fam1/abc` won't open as `file/fam1/xyz`, so encrypted blobs can't be swapped or replayed into another slot.

**Q: Why is the nonce random, and is 96 bits enough?**
A: GCM must never reuse a (key, nonce) pair. Random 96-bit nonces are safe for up to about 2³² messages per key. Our keys are per-file or per-family-epoch, far below that limit.

**Q: How does a joining phone get the family key securely?**
A: The joiner sends its public agreement key. The admin encrypts the family key with ECIES (`RecipientBox`) to that public key, so only the joiner's private key can open it. The relay only sees ciphertext. In remote invites, both people also compare a safety code to rule out a substituted key.

**Q: What happens if a phone is stolen?**
A: The database and files are encrypted under keys derived from a DMK that is wrapped by a hardware-backed, non-exportable Android Keystore key. Without the phone's secure hardware unlocking it, the data can't be decrypted. The app lock adds a UI barrier on an unlocked phone.

**Q: Why ECDSA signatures if messages are already encrypted with the family key?**
A: Every member has the family key, so encryption alone proves only "a family member wrote this". Signatures prove *which* member, which matters for admin-only actions (removing members) and for stopping a removed member from forging changes.

**Q: How does "erase everything" work so fast?**
A: Crypto-shredding. Deleting the Keystore wrapping key makes the DMK unrecoverable, so all data encrypted under it becomes noise. No need to overwrite every file.

## Possible improvements
- StrongBox-backed keys where the phone supports them (Device.Security will prefer StrongBox).
- Key rotation for the DMK (re-wrap per-file keys) as a maintenance task.
