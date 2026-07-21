# Nexus Guardian signing key

Run `./scripts/New-IntegrityKey.ps1` on a trusted offline Windows machine.
Commit only `integrity-public-key.pem`. Never commit `.guardian-key`, a private PEM,
PFX file, certificate password, crash-report credential, or GitHub token.

The official release workflow expects the Base64-encoded UTF-8 private PEM in the
GitHub Actions secret `NEXUS_INTEGRITY_PRIVATE_KEY_BASE64`.

## Crash-report encryption key

Run `./scripts/New-CrashReportKey.ps1` once on the trusted report-owner machine.
Commit only `security/crash-report-public-key.pem`. Back up
`.guardian-key/report-mail-v1/crash-report-private-key.pem` offline and never
upload it to GitHub, CI, a release, a tester machine, Proton, or Matrix.

The crash-report encryption key is independent from the Guardian integrity
signing key. Do not reuse either private key for the other purpose. Decrypt an
incoming `.ncrash` file with `./scripts/Read-CrashReport.ps1`.
