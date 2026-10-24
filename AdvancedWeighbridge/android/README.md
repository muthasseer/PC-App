# Advanced Weighbridge – Android companion app

A remote view of the AWMS cloud for managers and owners: site and weighbridge status, tickets with their PDF,
reports, alerts and cancellation requests. It never weighs, prints or controls lights and barriers; the site PC does.

| Folder | What it is |
|---|---|
| `core/` | Platform-independent Kotlin: JSON, API models, API client (token renewal), live/NOT LIVE rules, report periods. Unit-tested on the JVM. |
| `app/` | The Android app (framework UI built in code, no AndroidX). Compiles the `core/` sources too. |
| `app/strings/` | String table (English, Sinhala, Tamil) and the generator for `res/values*/strings.xml`. |

## Build

Requirements: JDK 17+, Android SDK with platform 36 (Android Studio installs it; the Gradle plugin downloads missing
SDK parts when the licences are accepted).

```sh
./gradlew -p core test            # core unit tests
./gradlew lintDebug assembleDebug # lint + debug APK  -> app/build/outputs/apk/debug/app-debug.apk
./gradlew assembleRelease         # release APK (signed only when a key is configured, see below)
```

Release signing: create `keystore.properties` next to this file (it is git-ignored):

```properties
storeFile=/path/to/awms-release.jks
storePassword=...
keyAlias=awms
keyPassword=...
```

or set `AWMS_KEYSTORE_FILE`, `AWMS_KEYSTORE_PASSWORD`, `AWMS_KEY_ALIAS`, `AWMS_KEY_PASSWORD`. CI uses the repository
secrets `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`; without
them it publishes the debug APK (for testing) and an unsigned release APK.

After editing `app/strings/strings.py`, run `python3 app/strings/generate.py`.

## Behaviour that matters

* **LIVE vs NOT LIVE.** A weight is shown as LIVE only when the site's last heartbeat plus the time since the phone's
  last successful refresh is at most 90 seconds. Otherwise the tile says NOT LIVE, dims the value and states its age.
  A failed refresh is shown above the tiles. The dashboard refreshes every 10 s and re-evaluates freshness every 5 s.
* **Manual weights** are labelled MANUAL in lists and "MANUAL ENTRY – not a verified indicator reading" in details.
* **Security.** HTTPS only (plain HTTP solely for `10.0.2.2`/`localhost` development). The refresh token is encrypted
  with an AES-GCM key held in the Android Keystore; passwords are never stored; nothing is included in backups.
  Two-factor codes are supported at sign-in; administrators who must use 2FA set it up in the web dashboard first.
* **Alerts** are polled by a JobScheduler job about every 15 minutes (Android's minimum) when the user turns them on;
  Android 13+ asks for the notification permission.
* **Cancellation** is a request: the site PC applies it when it next connects and reports the result.

## Status

CI builds and lints the app, runs the core unit tests, and runs `e2e/mobile_e2e.py` on an Android 15 emulator
against a real cloud server: sign-in with a forced password change, LIVE dashboard, tickets, ticket details,
in-app PDF, cancellation request (checked on the server), manual-entry labelling, reports, alerts, account, and
NOT LIVE after the site stops sending heartbeats. The screenshots are part of the release package. The app has not
yet been tried on physical phones — see test F4 in `docs/09-hardware-acceptance-tests.md`.

Run the end-to-end test locally (emulator running, cloud server on port 5080):
`python3 e2e/mobile_e2e.py --apk app/build/outputs/apk/debug/app-debug.apk --server http://localhost:5080 --device-server http://10.0.2.2:5080 --admin-password '<bootstrap password>'`
