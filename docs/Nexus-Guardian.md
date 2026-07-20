# Nexus Guardian

Nexus Guardian is the local trust and crash-recovery layer of Nexus Monach. It
does not replace Windows Defender, Authenticode, browser sandboxing or regular
updates. It makes accidental corruption, replaced runtime files and reproducible
crashes visible before the browser starts.

## Startup chain

1. `NexusMonach.exe` is the small Guardian launcher.
2. Guardian verifies the ECDSA P-256 signature of `integrity-manifest.json`.
3. On every start it hashes critical EXE, DLL, adapter and UI files. Large model
   weights and `node_modules` receive a quick size check; `--full-integrity-check`
   hashes the complete payload.
4. A valid build starts `NexusMonach.Browser.exe` with an isolated session ID and
   the integrity state. A critical mismatch blocks startup. A non-critical
   mismatch starts safe mode without local AI and extensions.
5. Three abnormal exits in ten minutes activate crash-loop safe mode. A recent
   WPF compositor out-of-memory crash also activates it for the next launch.
   Safe mode uses software rendering and disables WebView2 GPU compositing in
   addition to local AI and extensions; ordinary launches keep acceleration.
6. A signature or file mismatch is written as a sanitized local integrity event
   in the same Crash Vault used by crash reports. Identical integrity events are
   deduplicated for 24 hours.

The integrity manifest is created after optional Authenticode signing, because
signing changes the executable bytes. Repair is deliberately not automatic in
this version: a damaged critical file requires a fresh official archive.
The release public key is embedded into Guardian at build time; the external PEM
is retained for transparent inspection. Authenticode anchors the launcher to
Windows. Without Authenticode, the manifest still detects ordinary corruption,
but an attacker who can replace the launcher itself can replace the verifier too.

## Crash Vault

Managed WPF failures, unobserved task failures, WebView2 process failures and a
native browser exit without a clean session marker are recorded under:

```text
%LOCALAPPDATA%\NexusMonach\Guardian\CrashVault
```

The report contains the product/runtime version, OS version, process
architecture, technical stage, sanitized exception, integrity state, safe-mode
flag and up to 50 predefined technical breadcrumbs. It never intentionally
contains browsing history, full URLs, search queries, DOM/page text, cookies,
tokens, passwords, form values, IP/DNS/fingerprint data, audio, screenshots or a
full memory dump.

Managed fatal reports carry the random Guardian session identifier. When WPF or
.NET has already recorded the exact fatal exception, the launcher does not add a
second generic `native-process` report for the same exit. A native report remains
the fallback for access violations and exits that bypass managed crash handlers.

The development default is **Local only**. Reports remain on this computer and
are available from `Menu -> Nexus Guardian · local reports`. The local center
shows integrity and safe-mode state, opens sanitized reports, creates a harmless
diagnostic report, runs a full on-demand hash check, and supports copy, JSON
export and deletion. None of these
actions uses the network. Automatic anonymous upload, when explicitly enabled
in a future tester build, uses the same sanitized JSON. The repository does not
embed a GitHub token or a third-party telemetry SDK.

This local-first stage is intentional: the owner can test report quality on one
machine before enabling a relay for other testers. The Crash Vault schema stays
compatible with the future server transport.

## Reports from testers to Matrix

Do not put a Matrix access token into a distributed browser build. Anyone who
receives the build could extract the token and impersonate the bot. Nexus uses a
small open-source relay for tester builds:

```text
tester browser -> HTTPS -> Nexus Guardian Relay -> Matrix Client-Server API
```

The relay lives in `src/Nexus.Guardian.Relay`. It does not keep reports or client
IP addresses in an application database, accepts at most 128 KiB, validates an
allow-listed schema, rate-limits input and deduplicates repeated crashes. The
Matrix token exists only in the server environment. Configure and run it behind
a TLS reverse proxy:

```powershell
docker compose -f src/Nexus.Guardian.Relay/docker-compose.example.yml up -d --build
```

Server environment variables:

```text
Guardian__Matrix__Homeserver=https://matrix.example.org
Guardian__Matrix__RoomId=!internal-room-id:example.org
Guardian__Matrix__AccessToken=<bot token; server secret only>
Guardian__IngestKey=<limited, rotatable ingest key>
```

Use an ordinary private room in this release. If `m.room.encryption` is enabled,
the relay refuses to leak a plaintext report into that room. Proper E2EE requires
an Olm/Megolm Matrix client and is a separate implementation stage.

After the relay is available at HTTPS, set repository variable
`NEXUS_GUARDIAN_REPORT_ENDPOINT` to its `/api/v1/crash-reports` URL and secret
`NEXUS_GUARDIAN_REPORT_INGEST_KEY` to the relay ingest key. The Full Offline
workflow writes these values to the signed `guardian-reporting.json`. The ingest
key can be recovered from a public test build, so it is deliberately limited to
report submission and must never be the Matrix token. Rotate it if a build is
abused.

Set repository variable `NEXUS_GUARDIAN_REPORT_MODE` to `ask` (privacy-first
default) or `automatic`. Automatic mode is applied only when a tester creates a
new browser profile; it does not silently overwrite an existing user's choice.
Tell testers about automatic crash reporting before distributing such a build.

For a private local installation, settings also offer direct Matrix delivery.
Its token is kept in Windows Credential Manager. This mode is not appropriate
for a build distributed to testers.

## Official signing setup

For a normal local build, `RUN_BUILD.cmd` creates a per-workstation ECDSA
development key on first use, stores it in the Git-ignored `.guardian-key`
directory, signs the portable manifest and performs a full verification before
creating the ZIP. The private key is never copied into `dist` and must not be
shared. Later local builds reuse the same key. Official releases use a separate
key from the protected CI secret and do not trust this development key.

Run this once on a trusted Windows machine:

```powershell
./scripts/New-IntegrityKey.ps1
```

Commit only `security/integrity-public-key.pem`. Never commit the private PEM.
Encode the private PEM as Base64 UTF-8 and store it in the GitHub Actions secret
`NEXUS_INTEGRITY_PRIVATE_KEY_BASE64`:

```powershell
$bytes = [IO.File]::ReadAllBytes('.guardian-key\integrity-private-key.pem')
[Convert]::ToBase64String($bytes) | Set-Clipboard
```

The Full Offline workflow signs the final manifest and immediately performs a
full verification before publishing the ZIP. Until the official key is
configured, CI builds are explicitly marked `development-unverified`; local
builds still exercise integrity verification with their per-workstation key but
are not equivalent to an officially signed release.

The release workflow compiles Guardian with `GUARDIAN_OFFICIAL`; that binary
refuses to start without a valid signed manifest. Ordinary source builds omit the
flag and remain usable for development without a private release key.

## WebView2 runtime monitoring

Guardian Center shows the WebView2 Runtime version used by the current browser
process and the newest Evergreen Runtime version already installed on Windows.
It also subscribes to `NewBrowserVersionAvailable`. If Microsoft Edge Update
installs a newer runtime while Nexus Monach is open, Guardian marks that a full
browser restart is required. The check is local and does not send browsing or
device data, download an installer, change Windows Update policy or restart the
browser automatically. Evergreen Runtime installation and servicing remain
under Microsoft Edge Update and the user's Windows administration policy.

When an already installed Evergreen update is ready, the normal browser window
and Guardian Center offer a safe restart. Before closing, Nexus captures at most
20 ordinary tabs, their scroll positions and a bounded set of non-sensitive form
controls. The one-shot snapshot is encrypted with Windows DPAPI for the current
Windows user and expires after two hours. It is never uploaded and is removed
after a successful restore.

Nexus deliberately excludes private windows, passwords, OTP and verification
codes, email and telephone inputs, payment/banking fields, file controls and
pages whose path indicates sign-in, OAuth, checkout or billing. Authentication
cookies and site storage are not copied into the restart snapshot: they remain
in the normal WebView2 profile, where Windows and Chromium protect them. Some
sites rebuild forms dynamically, so restoring ordinary field values is best
effort; if the encrypted snapshot cannot be written, Guardian cancels the
restart and leaves the current window open.

## Local Sledopyt diagnostics

Guardian Center can display a bounded local journal for the Nexus Sledopyt crawl
engine. It records only operation/stage identifiers, outcome, duration and
candidate/result counters. Search queries, URLs, domains, page text, DOM, form
values and exception messages are deliberately not accepted. The journal is not
part of network crash-report delivery and remains under the local application
data directory.

## Command line

```powershell
NexusMonach.exe --full-integrity-check
NexusMonach.exe --verify-only . --full-integrity-check
./scripts/New-IntegrityManifest.ps1 -Directory dist\NexusMonach-Portable -PrivateKeyPath <private.pem>
```

Normal startup performs the fast verification: SHA-256 for executable, managed,
adapter and UI files plus size checks for large model payloads. Use
`--full-integrity-check` when validating a release or after copying the archive;
it hashes every tracked file, including local AI models.

After backing up local work, the complete crash pipeline can be tested without
damaging files. This intentionally exits the browser and creates one sanitized
report in Crash Vault:

```powershell
dist\NexusMonach-Portable\NexusMonach.exe --guardian-test-crash
```
