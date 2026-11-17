# Releasing an Update

Phones running Travel Vault find new versions in **Settings → Check for updates** (and automatically once a day on Wi-Fi). They read the latest release of **github.com/SaiSanthosh97/travel-vault**, download the `.apk`, verify it against GitHub's SHA-256, and hand it to Android's installer.

## Steps for each new version

1. **Bump the version** in `src/Apps/TravelVault.App/TravelVault.App.csproj`:
   - `ApplicationDisplayVersion`: the user-visible version, e.g. `0.1.0` → `0.2.0`. It **must match the release tag** (`v0.2.0`), because the app compares them.
   - `ApplicationVersion`: a whole number that must go **up by one** every release (Android refuses to "update" to a lower or equal number).
2. **Record the change** in `docs/CHANGES.md` (and a technical doc if it's a feature).
3. **Test and build:**
   ```sh
   dotnet test tests/Core.Sync.Tests        # and the other test projects
   dotnet publish src/Apps/TravelVault.App -f net10.0-android -c Release
   ```
   The release build is signed automatically with the key in `%USERPROFILE%\.travelvault\` (see below).
4. **Commit and push**, then **publish the release** with the APK attached (the file name must end in `.apk`):
   ```sh
   copy src\Apps\TravelVault.App\bin\Release\net10.0-android\publish\io.github.saisanthosh97.travelvault-Signed.apk travel-vault-0.2.0.apk
   gh release create v0.2.0 travel-vault-0.2.0.apk --title "Travel Vault 0.2.0" --notes "What's new…"
   ```

Within a day (or straight away via "Check for updates"), phones offer the update.

## The signing key: back it up

`%USERPROFILE%\.travelvault\release.keystore` and `signing.props` (which holds its password) sign every release.
- **Android only installs an update signed with the same key** as the installed app. If this key is lost, no installed phone can ever be updated again: everyone would have to uninstall and reinstall (and restore from backup or rejoin their family).
- Keep a copy of that folder somewhere safe and private (e.g. an encrypted USB stick or password manager). **Never commit it.**

## Notes
- The repository must stay **public**, because the app reads releases without any login.
- Test builds made before v0.1.0 were signed with the development key; a phone with one of those must uninstall it once before installing v0.1.0.
