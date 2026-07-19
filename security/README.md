# Nexus Guardian signing key

Run `./scripts/New-IntegrityKey.ps1` on a trusted offline Windows machine.
Commit only `integrity-public-key.pem`. Never commit `.guardian-key`, a private PEM,
PFX file, certificate password, crash-report credential, or GitHub token.

The official release workflow expects the Base64-encoded UTF-8 private PEM in the
GitHub Actions secret `NEXUS_INTEGRITY_PRIVATE_KEY_BASE64`.
