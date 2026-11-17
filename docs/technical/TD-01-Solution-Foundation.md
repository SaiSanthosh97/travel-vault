# TD-01 · Solution Foundation and Shared Kernel

## Summary
Travel Vault is a .NET 10 solution made of **small component libraries** plus one thin MAUI Android app, following the same model as *Use It Before*. Business rules live in plain .NET libraries (`Core.*`) that can be unit-tested on any PC; persistence is in `Storage.*`; Android-specific code is in `Device.*`. A tiny shared kernel, `Core.Common`, provides the time source, record ids, the **Hybrid Logical Clock** used to version every synced record, and one JSON configuration shared by storage and sync.

## Where the code is
| File | Purpose |
|---|---|
| `TravelVault.slnx` | Solution (new XML solution format, readable and merge-friendly) |
| `Directory.Build.props` | Settings shared by every project: namespaces, nullable, warnings as errors, central packages, `InternalsVisibleTo` for tests |
| `Directory.Packages.props` | **Every NuGet version in one place** (central package management) |
| `tests/Directory.Build.props` | Common test setup (xUnit, coverlet) so test projects are 5 lines each |
| `src/Components/Core.Common/Clock.cs` | `IClock`, `SystemClock`, `ManualClock`, `Ids` |
| `src/Components/Core.Common/HybridTimestamp.cs` | `HybridTimestamp` + `HybridClock` |
| `src/Components/Core.Common/IVersioned.cs` | Contract for synced records + `Touch`/`Wins` helpers |
| `src/Components/Core.Common/Json.cs` | `JsonDefaults` (enums as text, HLC as text) |

## How it works

### Layering (Clean Architecture)
```
TravelVault.App (MAUI: Views, ViewModels, app Services)
      │ uses
      ▼
Core.* (domain + application rules, plain .NET)  ◀── Storage.Sqlite implements Core repository interfaces
      ▲                                           ◀── Device.* implement Core device interfaces (OCR, Keystore…)
      └── Core.Common (shared kernel: no dependencies)
```
**The dependency rule:** inner layers never reference outer ones. `Core.Documents` defines `IDocumentRepository`; `Storage.Sqlite` implements it. The domain never mentions SQLite, JSON columns or Android.

### Build rules
- `TreatWarningsAsErrors=true`: a warning (e.g. a possible null dereference) fails the build, so problems can't pile up.
- `Nullable=enable`: the compiler tracks which references can be null.
- `ManagePackageVersionsCentrally=true`: projects say *which* packages they use (`<PackageReference Include="xunit" />`), and `Directory.Packages.props` says *which version*. One upgrade updates every project, and two projects can't drift to different versions.
- `InternalsVisibleTo` is added automatically for `<Project>.Tests`, so tests can check internal helpers (e.g. `PassengerMatcher.EditDistance`) without making them public.

### Ids
`Ids.New()` returns a **UUID version 7** (`Guid.CreateVersion7()`) as 32 hex characters. Version-7 GUIDs start with a timestamp, so new ids sort after older ones. That keeps database index inserts at the "end" (better locality than random GUIDs), and ids are unique across phones without any coordination. `Ids.IsValid` checks the format before an id from another phone is used in a file path.

### Hybrid Logical Clock (HLC)
Every synced record carries a `Version` of type `HybridTimestamp(WallMs, Counter, Node)`.
- `HybridClock.Next()` returns a timestamp **greater than anything this device issued or has seen**:
  - if the wall clock moved forward → `(now, 0, node)`;
  - otherwise (same millisecond, or the clock was set back) → `(lastWall, lastCounter + 1, node)`.
- `HybridClock.Observe(remote)` is called for every received record, so the next local edit is ordered **after** it, even if this phone's clock is behind.
- Ordering: wall time, then counter, then node id (a deterministic tie-break, so every phone picks the same winner).
- Text form `000001765432100000-0001-node` is fixed-width, so **sorting the text sorts the versions**. That is useful in SQL and logs.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Component libraries per bounded context | Same proven model as Use It Before; each context can be tested and reused alone; clear ownership | One big project (blurs boundaries, slower tests); microservices (no server exists) |
| Domain in plain .NET, platform code behind interfaces | Domain tests run in milliseconds on any PC/CI without an emulator | Logic inside ViewModels (untestable without UI) |
| HLC for versions | Works with no central server; tolerates wrong phone clocks; simple total order | Wall-clock timestamps (a phone with a clock set a day ahead would always "win"); vector clocks (exact causality but grow with every device and need conflict UI); CRDTs (powerful but complex for this data size) |
| UUIDv7 ids | Created offline on any phone, no collisions, time-ordered | Auto-increment ints (collide across phones); random GUIDv4 (scattered index inserts) |
| `IClock` abstraction | Time rules (reminders, expiry, trips) need repeatable tests | `DateTime.Now` everywhere (untestable) |
| One `JsonDefaults` | A record serialises identically on disk and on the wire, so storage and sync can't disagree | Separate DTOs per layer (more mapping code, more bugs) |
| Central package management + warnings as errors | Consistency and early failure | Per-project versions |

## Small details worth knowing
- `ManualClock` lives in production code (not tests) because other components' tests need it too; it has no side effects.
- `HybridClock` uses the new .NET 9+ `System.Threading.Lock` type, which is cheaper and clearer than `lock(object)`.
- Enums are stored as **text** (`"Flight"`), not numbers, so adding a new enum value later never changes the meaning of old data.
- HLC `Observe` keeps the remote wall time but **this device's node id**, so issued timestamps always identify the issuing device.

## Edge cases
- Phone clock jumps backwards (manual change, NTP correction): versions still increase (counter path). Tested in `Next_does_not_go_backwards_when_wall_clock_is_set_back`.
- A peer with a clock days ahead: we move past its timestamps, so our later edits still win. Tested in `Observe_moves_past_a_remote_timestamp…`.
- Malformed version text from a peer: `TryParse` returns false and the JSON converter yields `Zero`, which loses every comparison, so it can never overwrite good data.

## Testing
`tests/Core.Common.Tests/HybridClockTests.cs`: monotonicity, clock set back, observing a fast clock, text round-trip and sort order, garbage rejection, tie-break, id validity.

## Interview Q&A
**Q: Why not just use `DateTime.UtcNow` as the record version?**
A: Phones' clocks are often wrong by minutes or more. With wall time, a phone whose clock is ahead would silently win every conflict, and a clock that jumps backwards could make a newer edit look older. An HLC stays close to real time (so "last writer wins" still means roughly the latest edit) but guarantees monotonicity and causality: an edit made after seeing another always sorts after it.

**Q: What is the dependency rule in your architecture?**
A: Source dependencies point inwards. Core libraries define interfaces (repositories, OCR readers, key vault); outer layers (SQLite, Android) implement them. That's what lets 100+ domain tests run without a phone.

**Q: Why UUIDv7 instead of GUIDv4?**
A: Both can be generated offline without collisions, but v7 embeds a timestamp prefix, so inserts land at the end of B-tree indexes instead of random pages, and ids roughly sort by creation time, which helps when debugging.

**Q: Why central package management?**
A: One file shows every dependency and version, which matters for the licence rule (MIT/Apache/BSD only) and makes upgrades atomic.

## Possible improvements
- Add analyzers (e.g. `Microsoft.CodeAnalysis.NetAnalyzers` at a stricter level) once the codebase stabilises.
- Source-generated JSON (`JsonSerializerContext`) to cut reflection and improve trimming on Android.
