# Licence and scheduled backup: contract

Code: `src/CobaltMrp.Api/Licensing/`, `src/CobaltMrp.Api/Backup/`. UI: Settings > **Licence** and **Backup** (Admin), plus the full-page "Licence required" screen.

## Licence
- Key format `ICL1.<base64url payload>.<base64url signature>`: RSA-SHA256 (PKCS#1 v1.5) over the payload text, made by the Cobalt License Manager (`C:\Claude\License`, product code **CobaltMRP**). The app holds only the embedded **public** key (`LicenseDefaults.PublicKeySpki`). The verification key can be swapped **only** when `ASPNETCORE_ENVIRONMENT=Development`, via `COBALT_LICENSE_TEST_PUBLIC_KEY` (tests). Never from settings.json or appsettings; Production ignores the variable.
- Valid = signature verifies, then product code equals `CobaltMRP` (case-insensitive), hardware key equals this machine's, expiry (UTC date) is today or later. Nothing in the payload is trusted or shown until the signature verifies.
- **Hardware key** = first 32 hex chars (upper case) of `SHA256("MachineName|ContentRootPath")`; the path has any trailing `\` removed. Installed service = `C:\CobaltMRP`. Moving the install folder or renaming the machine invalidates the licence.
- Stored in **`<install>\data\license.json`** (`{"LicenseKey":"..."}`), the folder a publish never touches. A corrupt file counts as unlicensed. `IsValid()` is cached 5 minutes; saving a key clears the cache.

### Endpoints
| Method, path | Who | Result |
|---|---|---|
| `GET /api/admin/license` | anyone (works unlicensed, before sign-in) | `{productCode, hardwareKey, state, isValid, licenseKeySet, expiresOn, customer, maxSeats, message}`. Never the key itself. `state`: Valid, NotLicensed, Malformed, BadSignature, WrongProduct, WrongMachine, Expired |
| `POST /api/admin/license` `{licenseKey}` | **Admin only** (401 anonymous, 403 others) | 200 + status when stored; **400** `{message, status}` when the key is not valid: an invalid key is never stored and never replaces a good one |

### The 402 gate
Every `/api/*` request answers **402** `{licenseRequired:true, reason, error, message}` while unlicensed, except: `/api/auth/*` (sign-in, status, setup, logout), `/api/admin/license`, `/api/admin/backup/*`. Static files are not gated. Because sign-in stays open, an administrator can always reach the licence page; saving a key stays Admin only (a fresh install creates its first admin from the server's browser, no licence needed).
UI: any 402 with `licenseRequired` (or a status check after sign-in) replaces the whole app with the Licence required screen (hardware key + copy, paste box, Check again, Sign out). A signed-in non-admin sees "Ask an administrator to enter a licence key".
Background: the scheduled-run ticker does nothing while unlicensed (one warning logged per lapse, state untouched, no crash). Scheduled **backups keep running**.

## Backup
- Zip `cobalt-mrp-backup-auto-yyyyMMdd-HHmmss-fff.zip` in the chosen folder (default `%ProgramData%\Cobalt\MRP\Backups\Scheduled`): `backup-info.json` (manifest incl. member list) + the catalog (`BackupCatalog`): `settings.json`, `users.json`, `automation-state.json`, `backup-schedule.json`, `license.json`, and the DataProtection key ring `keys/*` (without it the encrypted secrets in settings.json cannot be decrypted after a restore). Verified against the files a running instance actually creates. Add any new state file to the catalog.
- **Not included:** planned runs, forecasts, master-data overrides (SQL database "CobaltMrp"): take a SQL Server backup.
- Schedule in its own `data\backup-schedule.json`: enabled, `frequency` daily/weekly, `time` HH:mm (server local), `dayOfWeek` 0=Sunday, `folder`, `keepLast` 1-365 (default 14). Due-ness is derived from files on disk (due when the newest auto zip is older than the most recent scheduled time), checked every minute (first check 30 s after start), so restarts and downtime catch up at once. A failed run retries after 10 minutes.
- Retention deletes only `cobalt-mrp-backup-auto-*.zip` beyond the newest N; other files in the folder are never touched.
- Folder rules (on save, on run-now and in every run): absolute path; not under the app's `wwwroot`; not under `data\keys`; must be writable by the service account.
- **No restore endpoint, deliberately.** Restore is manual: stop the service, copy the files from the zip into `data\` (with `keys\`), start it, on the same machine (DPAPI-bound keys). `BackupCatalog.IsAllowedEntry` defines the exact allow-list a future restore must use.

### Endpoints (all Admin only; 401 anonymous, 403 Planner/Buyer; never licence-gated)
- `GET /api/admin/backup/schedule` -> `{settings, folder, defaultFolder, dataFolder, contents, nextRunLocal, running, last, backups[]}`
- `POST /api/admin/backup/schedule` (settings body) -> same view; 400 `{message}` for a bad folder
- `POST /api/admin/backup/run` -> `{result:{success,file,sizeBytes,pruned,error,trigger,...}, view}` (400 for a bad folder)
- `GET /api/admin/backup/download` -> the zip now (same writer)

## Deploy
`deploy\publish.ps1` publishes over the app folder and never touches `data\`, so `license.json`, `backup-schedule.json`, settings, users and keys survive (no per-file preserve list exists in `deploy\*.ps1`; nothing to add). A backup folder outside the app folder is also untouched.
