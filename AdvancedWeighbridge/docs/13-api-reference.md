# 13. Cloud API Reference (v1)

Base path: `https://<server>/api/v1`. JSON in UTF-8, property names in camelCase, `null` values omitted from
responses. Weights are **integer grams** (`…G` fields), times ISO-8601 UTC. The DTOs are defined in
`src/Weighbridge.Contracts/Contracts.cs` and shared by the desktop sync client, the server and (mirrored) the
Android core.

Errors: HTTP 4xx/5xx with body `{"error": "<message for the user>"}`; a sign-in that needs the authenticator code
returns 401 with `{"error": "…", "mfaRequired": true}`. Validation errors are 400, missing permission 403,
unknown objects 404, rate limiting 429.

## 13.1 Authentication (users: web dashboard, Android app)

| Method and path | Body / result |
|---|---|
| `POST /auth/login` | `{username, password, totpCode?}` → `{accessToken, expiresUtc, refreshToken, user}`. Rate-limited (20 requests per minute per IP address by default); 5 failures lock the account for 15 min. |
| `POST /auth/refresh` | `{refreshToken}` → new `{accessToken, refreshToken, …}`. Refresh tokens rotate on every use; presenting a replaced token revokes the whole token family (theft detection). |
| `POST /auth/logout` | `{refreshToken}` (authenticated) → revokes the token family. |
| `GET /me` | `user`: `{id, username, displayName, role, permissions[], sites[], mfaEnabled, mustChangePassword, mfaSetupRequired}`. |
| `POST /me/password` | `{currentPassword, newPassword}` → new login response; all other sessions of the user are revoked. |
| `POST /me/mfa/setup` | → `{secret, provisioningUri, qrPngBase64}` (TOTP, 6 digits, 30 s). |
| `POST /me/mfa/enable` | `{secret, code}` (the secret from setup and a current code) → enables two-factor sign-in. |
| `POST /me/mfa/disable` | `{password, code}` → disables it. |

Send the access token as `Authorization: Bearer <token>` (valid 15 minutes by default). While
`mustChangePassword` (or `mfaSetupRequired` for administrators when required) is set, only `/me…` and
`/auth/logout` are allowed.

## 13.2 Data for users

Every query is limited to the sites assigned to the user (administrators: all sites).

| Method and path | Permission | Description |
|---|---|---|
| `GET /sites` | dashboard.view | Sites: `{id, code, name, lastHeartbeatUtc, online, appVersion}`. |
| `GET /status?site=` | dashboard.view | Per site: `site`, `weighbridges[] {code, name, connection, liveG, stable, measuredUtc, displayStatus, operatingMode, automationState, lastError, pendingTransactions}`, `health {diskFreeGb, lastBackupUtc, syncPending, syncFailed, databaseProvider, warnings[]}`, `heartbeatAgeSeconds`, `liveDataStale`. `liveDataStale` is true (and `displayStatus` becomes `NotLive`) when the heartbeat or the reading is older than `LiveDataStaleSeconds` (90). Clients must not present such values as live. |
| `GET /transactions?site=&from=&to=&q=&status=&vehicle=&manualOnly=&page=&pageSize=` | history.view | Paged `{items[], total, page, pageSize}`; items `{uid, ticketNo, siteCode, status, vehicleReg, customer, supplier, material, grossG, tareG, netG, hasManualWeight, createdUtc, completedUtc, operatingMode}`. |
| `GET /transactions/{uid}` | history.view | `{summary, transaction, commands[]}`; `transaction` is the full synchronised record including `measurements[]` (`source` = `Indicator`, `Manual` or `StoredTare`, `manualReason`, `isStable`, `unstableOverride`, `overrideReason`, `rawText`, `profileName`, `profileVersion`, …). |
| `GET /transactions/{uid}/ticket.pdf` | history.view | Ticket PDF rendered from the synchronised record. |
| `POST /transactions/{uid}/cancel` | transaction.cancel | `{reason}` (≥ 5 characters) → remote command `{id, uid, type: "CancelTransaction", targetUid, reason, requestedBy, requestedUtc, status: "Pending", resultMessage}`. The site PC executes it (status `Delivered`, then `Completed` or `Failed` with the site's message). An open request is returned instead of creating a second one. |
| `GET /reports/summary?groupBy=&from=&to=&site=` | reports.view | Completed transactions by completion time, `groupBy` = `day` (configured time zone), `site`, `customer`, `supplier`, `material`, `vehicle`; default period: last 30 days. → `{groupBy, from, to, rows[] {key, count, netG}, totalCount, totalNetG}`. |
| `GET /reports/export?format=csv\|xlsx\|pdf&from=&to=&site=` | reports.export | Transactions list as a file. |
| `GET /notifications?afterId=&unreadOnly=&limit=` | dashboard.view | Newest first: `{id, siteCode, atUtc, severity, category, title, message, entityId, isRead}`. |
| `POST /notifications/read` | dashboard.view | `{ids: [..]}` or `{ids: null}` for all. |
| `GET /audit?site=&from=&to=&q=&category=&page=&pageSize=` | audit.view | Uploaded site audit events. |
| `GET /audit/verify?site=` | audit.view | Re-computes the uploaded hash chain → `{siteCode, checked, valid, firstLocalId, lastLocalId, brokenAtLocalId, message}`. |

## 13.3 Administration

| Method and path | Permission | Description |
|---|---|---|
| `GET /admin/sites`, `POST /admin/sites` `{code, name}` | cloud.manage | List; create → `{site, siteKey}` (the key is returned only here, stored as a SHA-256 hash). |
| `POST /admin/sites/{code}/rotate-key` | cloud.manage | New site key (the old one stops working immediately). |
| `POST /admin/sites/{code}/activate`, `/deactivate` | cloud.manage | A deactivated site cannot sync. |
| `GET /admin/conflicts`, `GET /admin/server-audit` | cloud.manage | Sync conflicts with payloads; server audit (sign-ins, admin actions, remote requests, IP). |
| `GET /admin/users`, `POST /admin/users` `{username, displayName, password, role, sites[]}` | users.manage | New users must change the password at first sign-in. |
| `PUT /admin/users/{id}` `{displayName?, role?, sites?, isActive?}` | users.manage | |
| `POST /admin/users/{id}/reset-password` `{newPassword}`, `/unlock`, `/reset-mfa` | users.manage | |
| `GET /admin/site-users` | users.manage | Operator accounts reported by the sites (names, roles, last sign-in; no password data). |

## 13.4 Site synchronisation (desktop → cloud)

Authenticated with the header `X-AWMS-Site-Key: <site key>`; rate-limited (1 200 requests per minute per site). Batches are `{items: [...]}`;
results are `{results: [{uid, status, serverVersion, message}]}` with status:

| Status | Meaning |
|---|---|
| `Accepted` | Stored (new, or newer version). |
| `Unchanged` | The server already has exactly this version (idempotent re-send). |
| `Stale` | The server has a newer version; ignored. |
| `Conflict` | Same version, different content; recorded for review, nothing overwritten. |
| `Rejected` | Invalid payload (message says why). |

| Method and path | Body |
|---|---|
| `POST /sync/heartbeat` | `{siteCode, siteName, appVersion, sentUtc, weighbridges[], health}` → `{serverUtc, pendingCommands}`. Sent every interval (30 s); the live weight only if the site allows it. |
| `POST /sync/transactions` | `SyncTransactionDto[]`: ticket fields, `version`, `status`, weights, `hasManualWeight`, `hasOverride`, tare method, users and times, `measurements[]`. Driver names only if sharing is enabled. |
| `POST /sync/notifications` | `{uid, atUtc, severity, category, title, message, entityType, entityId}[]`. |
| `POST /sync/audit` | `{uid, localId, atUtc, username, machine, category, action, entityType, entityId, summary, reason, prevHash, hash}[]` in chain order. |
| `POST /sync/users` | `{username, displayName, role, isActive, lastLoginUtc}[]`. Never passwords or hashes. |
| `GET /sync/commands` | Pending remote commands for this site (marks them `Delivered`). |
| `POST /sync/commands/{id}/ack` | `{success, message}`. |

## 13.5 Public

| Method and path | Description |
|---|---|
| `GET /version` | `{product, version, apiVersion, serverUtc}`. |
| `GET /health` | `{status: "ok"}` when the database is reachable. |
| `GET /verify?t=&u=&n=` and the page `GET /verify` | Ticket verification from the QR code: ticket number, uid and net grams must match the synchronised record; cancelled tickets are reported as such. Rate-limited (60 per minute per IP address); reveals only ticket number, site, status, vehicle, net weight and completion time of the matching ticket. |

Security headers on every response (CSP without inline scripts, `X-Content-Type-Options`, `Referrer-Policy`,
frame denial); HSTS is added by the reverse proxy in the Docker deployment.
