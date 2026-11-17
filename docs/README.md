# Family Travel Vault — Design Documents

Status: **Draft, awaiting sign-off** (2026-11-16). Decisions D1–D52 are in [../Requirements.md](../Requirements.md).

**In one line:** an Android app (.NET 10 MAUI) where each family phone keeps an encrypted copy of all travel documents and syncs end-to-end encrypted through a free relay (ntfy). It has no server, no accounts, no AI and costs nothing, following the Use It Before design.

| # | Document |
|---|---|
| 1 | [Product Requirements](01-PRD.md) |
| 2 | [User Journeys](02-User-Journeys.md) |
| 3 | [Domain Design](03-Domain-Design.md) |
| 4 | [Bounded Contexts & Solution Structure](04-Bounded-Contexts.md) |
| 5 | [Database Design](05-Database-Design.md) |
| 6 | [Offline Sync Strategy](06-Offline-Sync-Strategy.md) |
| 7 | [Security Model](07-Security-Model.md) |
| 8 | [MVP Scope](08-MVP-Scope.md) |
| 9 | [Future Scope](09-Future-Scope.md) |
| 10 | [Risks & Mitigations](10-Risks-and-Mitigations.md) |

## Points that need your sign-off
1. **Remote invites need admin approval + safety code** (refines D45). In-person QR stays instant. This is safer, because a leaked invite text alone can't get anyone in.
2. **"Share with selected members" moves to right after the MVP**; the MVP has Family / Private only.
3. **Passport/ID added** as a document type with expiry reminders.
4. **Strict mode** (optional): sync pauses while the app is locked, for maximum security.
5. **Encrypted database library:** SQLite3 Multiple Ciphers (MIT) preferred, decided by a short build spike.
