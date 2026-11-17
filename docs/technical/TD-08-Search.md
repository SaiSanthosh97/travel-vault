# TD-08 · Global Search

## Summary
One search box finds documents by city (any of its names), airport or station code, passenger (as printed or by profile name and aliases), provider, PNR/booking reference, flight/train number, or free text. It uses **SQLite FTS5** inside the encrypted database, with **prefix matching** and **weighted ranking**. It works fully offline and the index is kept up to date by vault events.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Search/Search.cs` | `SearchEntry` (what is indexed), `SearchEntries.For` (builder), `SearchQuery.ToFts` (safe query), `ISearchIndex` |
| `Storage.Sqlite/SqliteSearchIndex.cs` | FTS5 implementation with bm25 column weights |
| `Storage.Sqlite/Migrations.cs` | `search_index` virtual table |
| `Core.Vault/TripAndSearchServices.cs` (`SearchService`) | Query use case + `IVaultChangeHandler` that keeps the index current |
| `tests/Core.Search.Tests`, `tests/Core.Vault.Tests` | Builder/query tests + end-to-end searches |

## How it works

### What goes into the index (`SearchEntries.For`)
| Column | Content | Why |
|---|---|---|
| title | Title + type ("Flight · Bangalore → New Delhi Flight") | Type words are searchable ("hotel") |
| provider | "IndiGo", "Booking.com" | |
| refs | PNR, booking ref, identity number, every flight/train number **with and without spaces** | "6E2345" and "6E 2345" both match |
| places | Place names, codes, country codes, and **every alias of each city** from the bundled list | "Bengaluru", "Bangalore" and "BLR" all find the same flight |
| passengers | Names as printed + traveler profile names + aliases | Find by "Priya" even if the ticket says "SHARMA/PRIYAMRS" |
| notes | User notes | |

The table is created with `tokenize = 'unicode61 remove_diacritics 2'` (case- and accent-insensitive: "Zurich" finds "Zürich") and `prefix = '2 3'` (prefix indexes, so `del*` is fast).

### Turning user input into a query (`SearchQuery.ToFts`)
- Split on anything that isn't a letter or digit, keep up to 8 words, and wrap each as `"word"*` (quoted = literal, `*` = prefix).
- Words are AND-ed: `del 6e` → `"del"* "6e"*`.
- Because every word is quoted, **FTS syntax can't be injected**: `"; DROP TABLE` becomes `"DROP"* "TABLE"*`, just harmless words. The query is also passed as a SQL parameter, never concatenated.

### Ranking
`ORDER BY bm25(search_index, 0, 0, 2, 2, 5, 4, 3, 1)` weights the columns: **refs 5**, places 4, passengers 3, title 2, provider 2, notes 1 (the two id columns 0). An exact PNR hit beats a word in the notes.

### Keeping the index current
`SearchService` is an `IVaultChangeHandler`:
- a document changed → re-index it (or remove it if deleted);
- a traveler changed (e.g. renamed) → re-index the family, because names appear in many documents;
- `ReindexFamilyAsync` rebuilds from scratch (also used after a restore or when sync brings many changes).

Results are re-loaded from the documents table and deleted ones dropped, so the index can never show a deleted document even if it was briefly stale.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| SQLite FTS5 | Built into the encrypted SQLite build; fast; ranking included; offline; no extra library | LINQ `Contains` over all documents (slow, no ranking); Lucene.NET (large dependency) |
| Index inside the encrypted DB | The index holds names and PNRs, so it must be encrypted like everything else | A separate plain index file |
| Prefix + quoted words | Search-as-you-type feel, with no injection | Raw FTS syntax from the user |
| City aliases at index time | Queries stay simple; the index knows all the names | Expanding queries at search time |
| Weighted columns | References are the most precise thing a user types | Equal weights |

## Interview Q&A
**Q: How does "Bengaluru" find a ticket that says "Bangalore (BLR)"?**
A: When indexing, every place gets its code, its name and all known aliases of its city from a bundled table. The index entry for that flight contains Bangalore, Bengaluru and BLR, so any of them matches.

**Q: Isn't building FTS queries from user input dangerous?**
A: It would be if we passed it raw. We split the input into letters/digits words, quote each one (so it's a literal term) and pass the whole query as a parameter. The worst a user can do is search for odd words.

**Q: How is the index kept consistent with the data?**
A: Event-driven. Every saved change publishes a `VaultChange`, and the search handler re-indexes that document. Results are joined back to the live documents table, so even a stale index entry can't surface a deleted document.
