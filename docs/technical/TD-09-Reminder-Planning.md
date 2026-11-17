# TD-09 · Reminder Planning

## Summary
`Core.Reminders.ReminderPlanner` computes **every notification the phone should show**, from the documents already on the phone. There is no server and no push service. Each phone plans its own reminders, which therefore work offline and cost nothing (D38). The plan is **recomputed completely after every change**, and each reminder has a **stable key**, so re-planning updates instead of duplicating. Scheduling the plan with Android alarms is `Device.Reminders`' job (later phase).

## Where the code is
| File | Purpose |
|---|---|
| `Core.Reminders/ReminderPlanner.cs` | `PlannedReminder`, `ReminderKind`, `ReminderSettings`, `ReminderPlanner.Plan` |
| `tests/Core.Reminders.Tests/ReminderPlannerTests.cs` | 7 tests |

## How it works
`Plan(documents, travelers, myMemberId, settings, now, phoneZone, lastBackup)` returns reminders sorted by time:

| Kind | When | Text example |
|---|---|---|
| Departure | **24 h** and **3 h** before each segment's departure (flight/train/bus), check-in or event start | "Flight to New Delhi in 3 hours" / "6E 2345 · BLR → DEL · 12 Dec 2026, 06:10 · PNR X7KQ2P · Seat 14C" |
| Missing boarding pass | 24 h and 6 h before each **flight** segment that still has travelers without a linked pass | "Boarding pass missing for Rahul, Priya" |
| Trip tomorrow | **6 pm phone time** the evening before a document's first segment (skipped if that's less than 6 h before departure) | "Tomorrow: Flight · Bangalore → New Delhi" with travelers' names |
| Identity expiry | 90, 30 and 7 days before a visa/passport expires, at 10:00 | "Passport of Rahul expires in 90 days" |
| Backup | 30 days after the last backup, at 10:00 | "Back up your travel vault" |

Details:
- **Real time across zones:** departure instants come from `LocalDateTimeZ.ToUtc()` with the *place's* time zone, so a 06:10 Bengaluru flight reminds at 03:10 Bengaluru time even if the phone is in London.
- **Past times are dropped:** a reminder whose time has passed is never scheduled.
- **"My travel only" setting:** keep documents the user added, or where one of the travelers is linked to their member; otherwise all family travel (default, persona "Organizer").
- Deleted documents produce nothing. Linked boarding passes produce nothing (the ticket's reminders cover them).
- **Stable keys** such as `dep:<doc>:<segment>:3` or `bp:<doc>:<segment>:24`: when a flight time changes, the same key gets a new fire time. Android replaces the alarm, so there's no duplicate and no stale reminder (tested).

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Local planning on every phone | No server/push service needed; works offline; free | Firebase Cloud Messaging (needs a server to send) |
| Recompute the whole plan on any change | Simple and always correct; a family vault has at most hundreds of documents, so it takes milliseconds | Incremental alarm updates (complex, easy to leave stale alarms) |
| Stable keys | Idempotent scheduling: re-planning is safe to run any number of times | Random ids per run |
| Planner in Core, scheduler in Device | The rules are unit-testable on a PC; Android specifics (exact alarms, reboot restore) are isolated | Rules in Android code |
| Lead times 24 h / 3 h, boarding pass 24 h / 6 h | 24 h is when most web check-in opens; 3 h is "leave for the airport" time | User-configurable lead times (future option) |

## Interview Q&A
**Q: How do family-wide notifications work without a server?**
A: Every phone has the whole family's data, so every phone can compute "Priya flies tomorrow" itself and schedule a local notification. Nothing needs to be pushed.

**Q: What happens to a reminder when a flight is rescheduled?**
A: The updated ticket triggers a re-plan. Reminder keys are derived from the document, segment and lead time, so the same key gets the new time, and the Android scheduler replaces the existing alarm with that key.

**Q: How do you handle time zones?**
A: Departure times are stored as printed (local) with the place's IANA zone, converted to UTC for scheduling, and the phone shows the notification at that instant wherever it is. "Trip tomorrow" is deliberately in the phone's zone, since it's about the user's evening.
