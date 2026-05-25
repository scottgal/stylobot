# Release signing

How StyloBot release artifacts get cryptographically attested, and how end users verify them.

## What's signed today

| Artifact | Signing | Verification path |
|---|---|---|
| Windows `.exe` (`stylobot-win-*.zip`) | **Azure Trusted Signing** - Microsoft's cloud-managed code-signing service, RFC 3161 timestamped | Right-click → Properties → Digital Signatures in Windows; or `signtool verify /pa stylobot.exe` |
| Linux/macOS tarballs (GitHub Releases) | **SLSA Build Provenance Attestation** - keyless sigstore attestation linking the binary back to the exact workflow run + commit hash | `gh attestation verify stylobot-linux-x64.tar.gz --owner scottgal` |
| `SHA256SUMS.txt` (all platforms) | **GPG detached signature** (when `RELEASE_GPG_PRIVATE_KEY` secret is populated - see task #46) | `gpg --verify SHA256SUMS.txt.asc SHA256SUMS.txt` then `sha256sum -c SHA256SUMS.txt` |
| Cloudsmith apt repo (`stylobot-linux-x64.deb`) | **Cloudsmith repo signing** - `Release.gpg` signed with Cloudsmith's repo key per their managed signing infrastructure | Automatic via `apt update` - apt rejects unsigned repos by default |
| macOS native dylibs (`libe_sqlite3.dylib`) | **Ad-hoc linker signature only** - not Developer-ID-signed (task #45) | Gatekeeper refuses to dlopen on quarantined downloads; see "macOS first-run" below |

## Verifying a release as an end user

### Linux tarball

```bash
# Download the tarball + the SLSA attestation manifest (GitHub stores it)
curl -L -o stylobot-linux-x64.tar.gz \
  https://github.com/scottgal/stylobot/releases/download/allbot-vX.Y.Z/stylobot-linux-x64.tar.gz

# 1. Verify SLSA provenance (free, no key import needed)
gh attestation verify stylobot-linux-x64.tar.gz --owner scottgal

# 2. (Optional) Verify GPG-signed checksums when the release key is in use
curl -L -o SHA256SUMS.txt     .../SHA256SUMS.txt
curl -L -o SHA256SUMS.txt.asc .../SHA256SUMS.txt.asc
gpg --keyserver hkps://keys.openpgp.org --recv-keys <KEY_ID>
gpg --verify SHA256SUMS.txt.asc SHA256SUMS.txt
sha256sum -c SHA256SUMS.txt --ignore-missing
```

### macOS tarball (first-run quarantine note)

Browsers add `com.apple.quarantine` to downloaded files. The bundled native libs (`libe_sqlite3.dylib`) ship ad-hoc signed, and Gatekeeper refuses to dlopen quarantined ad-hoc dylibs (you'll see *"library load disallowed by system policy"*).

```bash
tar xzf stylobot-osx-arm64.tar.gz
cd stylobot-osx-arm64
./clear-quarantine.sh                # bundled helper
# or
xattr -dr com.apple.quarantine .     # same effect
./stylobot --help
```

This goes away when task #45 lands - Apple Developer ID + notarisation makes the tarball pass Gatekeeper from a fresh download.

**Easier**: install via Homebrew, which strips quarantine automatically:

```bash
brew install scottgal/stylobot/stylobot
```

### apt (Cloudsmith repo) - Linux

Already signed at the repo level by Cloudsmith. `apt-secure` verifies on every `apt update`. No manual action required.

```bash
curl -1sLf 'https://dl.cloudsmith.io/public/mostlylucid/stylobot/setup.deb.sh' | sudo bash
sudo apt update && sudo apt install stylobot
```

## Generating the release-signing GPG key (task #46)

One-time setup, ideally on an offline machine.

```bash
# 1. Generate an Ed25519 signing key with a 5-year expiry
gpg --quick-generate-key "Mostlylucid Releases <releases@mostlylucid.net>" ed25519 sign 5y

# 2. Note the key id
gpg --list-secret-keys --keyid-format=long
#    pub   ed25519/ABCDEF1234567890 2026-05-16 [SC] [expires: 2031-05-15]

# 3. Export the public key - commit to repo root + publish to keyservers
gpg --armor --export ABCDEF1234567890 > STYLOBOT_SIGNING_KEY.asc
gpg --keyserver hkps://keys.openpgp.org --send-keys ABCDEF1234567890
git add STYLOBOT_SIGNING_KEY.asc && git commit -m "chore: publish release-signing public key"

# 4. Export the private key - store as a GitHub secret, never commit
gpg --armor --export-secret-keys ABCDEF1234567890 > release-private.asc
# In GitHub → Settings → Secrets and variables → Actions:
#   RELEASE_GPG_PRIVATE_KEY  = (paste contents of release-private.asc)
#   RELEASE_GPG_PASSPHRASE   = (the passphrase you set during generation)
rm release-private.asc   # don't leave it on disk
```

After secrets are populated, the next release will produce `SHA256SUMS.txt.asc` alongside the tarballs. Until then, the `Sign checksums (GPG)` step in the workflow is a no-op (guarded by `if: env.HAS_GPG_KEY == 'true'`).

**Key rotation**: every 2-3 years. Bump expiry on the public key annually (`gpg --quick-set-expire ABCDEF1234567890 5y`).

## Setting up Apple Developer ID signing (task #45)

The walkthrough lives on the task itself. Summary:

1. Apple Developer Program enrollment ($99/yr) at developer.apple.com/programs
2. Create a "Developer ID Application" certificate; export from Keychain as `.p12`
3. Create an App Store Connect API key for `notarytool` (Issuer ID + Key ID + `.p8`)
4. Add five GitHub secrets, then unguard the codesign + notarytool steps in `publish-stylobot.yml`

Once shipped, downloaded macOS tarballs pass Gatekeeper without user action.

## Verifying SLSA attestation locally

The `gh` CLI handles this transparently:

```bash
gh attestation verify stylobot-linux-x64.tar.gz \
  --owner scottgal \
  --repo scottgal/stylobot
```

What it verifies:
- The artifact's SHA256 matches one in a sigstore-rekor-logged attestation
- The attestation was produced by `scottgal/stylobot`'s `publish-stylobot.yml` workflow
- The workflow ran on a specific commit you can inspect
- The attestation was signed by GitHub's keyless OIDC identity (no human in the trust path)

Conformance: this matches **SLSA Build Level 3** - the build platform (GitHub Actions) is trusted but the build itself runs in a tamper-evident environment, and the attestation is non-forgeable without compromising GitHub OIDC.