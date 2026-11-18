# Technical Design Documents

One document per implemented feature (D55). Each explains **what** was built, **how** it works down to the small details, **why** each approach was chosen over the alternatives, the trade-offs, edge cases, how it is tested, and likely **interview questions with answers**.

| # | Feature | Components | Status |
|---|---|---|---|
| [TD-01](TD-01-Solution-Foundation.md) | Solution foundation: structure, build rules, shared kernel (clock, ids, hybrid logical clock, JSON) | `Core.Common`, build files | Implemented |
| [TD-02](TD-02-Security-and-Keys.md) | Security & key management: encryption, signatures, key hierarchy, recovery code, safety code, file vault | `Core.Security` | Implemented |
| [TD-03](TD-03-Domain-Model.md) | Domain model: families & invites, travelers & name matching, documents, merging, conflict resolution | `Core.Families`, `Core.Travelers`, `Core.Documents` | Implemented |
| [TD-04](TD-04-Encrypted-Storage.md) | Encrypted on-device database, schema, repositories, full-text search table | `Storage.Sqlite` | Implemented |
| [TD-05](TD-05-Document-Parsing.md) | Reading tickets without AI: extraction, BCBP, MRZ, dates/names toolkit, classifier, per-type parsers | `Core.Parsing` | Implemented |
| [TD-06](TD-06-Import-and-Duplicates.md) | Import flow, exact & fuzzy duplicates, application layer and domain events | `Core.Vault`, `Core.Duplicates` | Implemented |
| [TD-07](TD-07-Trips-and-Boarding-Passes.md) | Trip suggestions, status, boarding-pass linking and "missing" detection | `Core.Trips` | Implemented |
| [TD-08](TD-08-Search.md) | Global search with FTS5, city aliases, safe queries, ranking | `Core.Search`, `Storage.Sqlite` | Implemented |
| [TD-09](TD-09-Reminder-Planning.md) | Offline reminder planning with stable keys | `Core.Reminders` | Implemented |
| [TD-10](TD-10-Serverless-Sync.md) | Serverless sync: wire format, signed changes, cycle, conflicts, 12-hour window, files, joins, removal & key rotation | `Core.Sync`, `Core.Relay` | Implemented |
| [TD-11](TD-11-Backup-and-First-Launch.md) | First launch, unlock, erase everything (backup & restore removed by C-020) | `Storage.Sqlite`, `Core.Vault` | Partly superseded |
| [TD-12](TD-12-Android-App.md) | The Android app: screens, composition, share target, lock, reminders, background sync, device components | `TravelVault.App`, `Device.*` | Implemented |
| [TD-13](TD-13-Form-Reading-and-Ticket-Card.md) | Reading tickets as forms by word position (IRCTC/RailOne, IndiGo, MakeMyTrip, redBus, Booking.com) and the ticket-card screen | `Core.Parsing`, `Device.Vision`, app Document page | Implemented |

## How to use these documents for an interview
1. Start with the **one-paragraph summary** of each document. That is your "elevator pitch" for the feature.
2. Learn the **Key decisions** tables. Interviewers mostly ask "why X and not Y?".
3. Open the code files listed in each document and follow along. Every claim points to a real file.
4. Practise the **Interview Q&A** answers out loud in your own words.
5. Run the tests (`dotnet test TravelVault.slnx`) and read one test per feature. Tests are the best proof of behaviour.
