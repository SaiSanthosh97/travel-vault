# 9. Future Scope

All items keep the zero-cost and no-server rules (D32, D40) unless marked otherwise.

## Next (after MVP)
| Feature | Notes |
|---|---|
| Share with selected members | Per-document content key sealed to each chosen member's agreement key (ECDH). Same relay topic. |
| Home-screen widget "Next up" | Reuse `Device.HomeWidget` from Use It Before |
| Passport 6-month validity check | Warn when a trip abroad (country from the airport list) falls within 6 months of passport expiry |
| `.pkpass` (Wallet pass) import | Zip + JSON format, free to parse |
| Calendar export | Add a segment to the phone calendar via an Intent (no permissions needed) |
| Translations | Strings are already in resources (Hindi, Tamil, etc. on request) |
| Real-sample parser tuning | From redacted samples the user puts in `samples/real/` (D31) |

## Later
| Feature | Notes / cost check |
|---|---|
| iOS app | MAUI makes the code portable, but App Store distribution costs $99/year. **Not free**, so it needs a decision. |
| Windows app | Free (MSIX sideload), as planned for Use It Before |
| Gmail auto-import | Needs Google restricted-scope verification (a paid security assessment). Not free unless kept to ≤ 100 test users in "testing" mode. |
| Forward-to-address email import | Needs an email-receiving service; free tiers exist but it adds a server dependency |
| Nearby sync (Wi-Fi Direct/Bluetooth) | Phones in the same home sync without internet; free (Google Nearby Connections) |
| Self-hosted relay guide | One-command ntfy Docker setup for families who want longer retention |
| Optional AI parsing | Bring-your-own key or local model; off by default (D34 keeps the MVP free of AI) |
| Live flight status | Free APIs are very limited; would need a paid one |
| Family travel history / stats | Countries visited, trip timeline |
