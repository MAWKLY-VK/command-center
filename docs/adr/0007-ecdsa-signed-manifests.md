# Sign the update manifest, the GO profile and the map catalog with ECDSA P-256; no Authenticode

The forced update trusts a SHA-256 that comes from the same `update.json` it protects. Whoever can change a release
can therefore push code to every user. From 0.5.0:

- `update.json` carries an ECDSA P-256 `signature`. The old fields stay as they are for 0.4.0.
- The public key is built into the app.
- The download URL must be under `github.com/MAWKLY-VK/command-center/releases/`.
- Older versions are refused, and every manifest has an expiry date.

`go-profile.json` and the map catalog get a `.sig` file next to them, so 0.4.0 copies keep reading the unchanged
originals.

Authenticode is not used. The SmartScreen warning is accepted and explained in the README and the release notes.

## Considered Options

- Authenticode code signing. Rejected: it needs a paid certificate and an identity check, and it still would not
  authenticate `update.json`, `go-profile.json` or the catalog.

## Consequences

- The private key is a password-encrypted PKCS#8 file, kept outside the repository and outside GitHub, with two
  offline backups.
- If the key is lost, installed 0.5.0+ copies can never accept another update, and players would have to download a
  new copy by hand. The backups exist to prevent that.
- If the key leaks, publish at once a release signed with the old key that carries a new public key, then sign only
  with the new key. Access to the repository and the URL restriction remain a second barrier. This procedure has
  applied since the key was created on 2026-11-05 (setup step 10).

## Format

Added on 2026-11-05 (phase 3). 0.5.0 and later verify exactly these bytes, so the format does not change:

- **Algorithm:** ECDSA P-256 with SHA-256. The signature is in IEEE P1363 form (64 bytes) and is written as base64.
- **Signed message:** the UTF-8 text `command-center:<kind>`, a line feed, then the content bytes.
  - The kinds are `update`, `go-profile` and `map-catalog`, so a signature for one kind never passes for another.
- **Signature files:** `go-profile.json.sig` and `catalog.json.sig` hold the base64 signature on one line, next to
  their file. Their content is the file's exact bytes.
- **`update.json`:** it keeps its old fields (`version`, `url`, `sha256`, `notes`) for 0.4.0, and adds two fields.
  - `signed` is the base64 of a UTF-8 JSON payload with `version`, `url`, `sha256`, `notes` and `expires` (UTC, one
    year after signing).
  - `signature` is the signature of kind `update` over the payload bytes.
  - A version that verifies reads only the payload.
- **Test builds:** a build made with `-p:TestKeys=true` also trusts a test key given with `--test-key <file>`.
  It is never published.

## Public key

Created on 2026-11-05 with `tools/New-SigningKey.cs`. The app embeds exactly this key, and a replacement key is
recorded here through the leak procedure above:

```
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwn5YzHoO5ed3WvhQ+UyGIJ/eacXk
SDLHjH9varfLZbZ2HzPJB4VeY5iPT/W4rrRasFpEeVGJqbUmxYGBvMKsAg==
-----END PUBLIC KEY-----
```

SHA-256 of its SubjectPublicKeyInfo (DER): `7167E066645BA73012D79201B9D8845AE2D407625985FBF2F52811B5F35C5233`

Decided with the user on 2026-11-04 (plan appendix B, phase 6; plan section 5).
