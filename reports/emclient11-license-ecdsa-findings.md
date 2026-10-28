# eM Client 11 license activation fails — Wine cannot import the server's ECDSA key

**Status:** root-caused and fixed. The patched assembly verifies the user's real v2 license token
under Wine; deployment wired in as **patch 7 / Stage 9** for eM Client 11.0.1267 and 11.0.1389.

## Symptom

Pressing **Activate** in eM Client 11 on Wine: the spinner runs, then the dialog goes back to its
previous state. No error dialog, no crash, and the app stays unlicensed. This is the same visible
behaviour as the eM Client 10 RSA-OAEP bug, but the cause in eM Client 11 is different.

## What the license token actually is

The activation call itself reaches the server and succeeds; eM Client stores its response in
`Program.Settings.LicenseInfo1`. Reading that setting out of the profile's SQLite database
(`settings.dat` → `GeneralSettings`) showed a **v2 ECDSA-signed envelope**:

```
v2.<base64url payload>.<base64url signature>
```

That is `LicenseFormatVersion.EcdsaSigned`, handled by
`Licensing.Encryption.DecryptAndVerify.DecodeAndVerifyStringV2` →
`EncodedLicenseEnvelopeCrypto.VerifyEnvelope`, which calls:

```csharp
verificationAlgorithm.VerifyData(envelope.GetUnsignedTokenBytes(), signature,
    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
```

No RSA decryption is involved for this format. The eM Client 10 OAEP fix alone would not have
helped.

## Root cause

`DecryptAndVerify`'s constructor creates a native ECDsa and imports the server's public key:

```csharp
signatureVerificationAlgorithmV2 = ECDsa.Create();
signatureVerificationAlgorithmV2.ImportFromPem(signatureVerificationKeyPem);
if (!EcdsaKeyValidator.IsNistP256(signatureVerificationAlgorithmV2)) throw ...
```

Under Wine that import always fails inside CNG:

```
System.Security.Cryptography.CryptographicException: Unknown error (0x80090029)
  at System.Security.Cryptography.ECCng.ImportKeyBlob(...)
  at System.Security.Cryptography.CngKey.Import(...)
  at System.Security.Cryptography.ECDsaCng.ImportKeyBlob(...)
  at System.Security.Cryptography.ECDsaCng.ImportParameters(...)
  at System.Security.Cryptography.ECAlgorithm.ImportSubjectPublicKeyInfo(...)
  at System.Security.Cryptography.PemKeyHelpers.ImportPem(...)
```

Reproduced directly with a small .NET 10 test program run inside the bottle
(`wine 'C:\Program Files\dotnet\dotnet.exe' test.dll`): the same PEM that verifies fine under
native Linux .NET throws `0x80090029` under Wine. So the constructor never gets past importing the
key, `EcdsaKeyValidator` never runs, and the surrounding blanket catch turns the exception into a
silent `false` — exactly the no-message/spinner-then-revert symptom.

The same assembly also still supports the older **RSA-encrypted** formats
(`DecryptAndVerifyStringV1` / `DecryptStringV0`, both calling
`RSACryptoServiceProvider.Decrypt(data, fOAEP: true)`), which hit the Wine/GnuTLS RSA-OAEP defect
already documented for eM Client 10 in `reports/license-activation-oaep-findings.md`. Both paths
need fixing, since a license can be either format.

## Fix

One helper assembly (`il-patches/MailClient.Licensing.BouncyCastlePatch/`), two managed pieces:

1. **`ManagedEcdsa`** — a pure-managed `ECDsa` subclass backed by BouncyCastle. It parses the PEM
   with `PemReader`/`PublicKeyFactory`, reports `KeySize = 256` and the nistP256 curve OID (so
   `EcdsaKeyValidator` passes), and implements `VerifyData`'s hash/`VerifyHash` path with
   `ECDsaSigner` over the IEEE P1363 `r || s` signature. No native CNG call anywhere.
   `EcdsaPatch.Create()` returns it.

2. **`OaepPatch.OaepSha1Decrypt`** — the existing pure-managed RSA-OAEP helper from the eM Client
   10 fix, reused unchanged.

`il-patches/license-encryption-patcher-Program.cs` then makes two minimal edits to
`Licensing.Encryption.dll`:

- retargets the single `ECDsa.Create()` call in `DecryptAndVerify`'s constructor to
  `EcdsaPatch.Create()` (same stack shape; the app's own `ImportFromPem`/`VerifyData` sequence is
  untouched);
- for each `RSACryptoServiceProvider.Decrypt(byte[], bool)` call with `fOAEP: true`, removes the
  `true` argument and retargets the call to `OaepPatch.OaepSha1Decrypt(RSA, byte[])`, with the
  usual branch/handler-boundary retargeting when the `ldc.i4.1` is removed.

`MailClient.deps.json` gets an explicit `MailClient.Licensing.BouncyCastlePatch/1.0.0` entry under
the file's own RID-qualified target (the helper's AssemblyVersion is the csproj default
`1.0.0.0`), and the helper DLL is deployed alongside the app.

## Verification

1. **Real token, real patched assembly, under Wine.** The user's stored v2 token was fed to the
   patched `Licensing.Encryption.dll` from a .NET 10 program running inside the CrossOver bottle:

   ```
   DecodeAndVerifyStringV2 ok=True len=1487
   TryVerifyAndDecode ok=True version=EcdsaSigned len=1487
   ```

   The same call against the unpatched assembly throws the `0x80090029` exception above. The
   decoded payload is the expected `<licensing>…` XML, so signature verification is genuinely
   correct, not just non-throwing.

2. **Fresh full pipeline runs** against both stable snapshots, x86, with every patch requested:

   | Build | Result |
   |---|---|
   | `original/em-11.0.1267/` | Stage 9 applied, all verification checks passed, mask **495** |
   | `original/em-11.0.1389/` | Stage 9 applied, all verification checks passed, mask **495** |
   | re-run on 1389 | marker read back as 495, pipeline correctly skipped |

   Stage 9 verification decompiles `DecryptAndVerify` (must reference `EcdsaPatch.Create` and at
   least two `OaepPatch.OaepSha1Decrypt` call sites), checks the helper DLL is staged,
   `--dump-handlers` on both RSA-patched methods, and checks the exact deps.json target entry.

## Layout changes

- `releases/11.0.1267/deploy.sh`: new stage bit `[9]=256`, patch `7 = stage 9`,
  `PIPELINE_LATEST_PATCH=7`, mandatory patches `1 2 3 4 6 7`; supports eM Client `11.0.1267` and
  `11.0.1389`; deploys `Licensing.Encryption.dll` and the helper.
- `il-patches/MailClient.Licensing.BouncyCastlePatch/ManagedEcdsa.cs` (new),
  `il-patches/license-encryption-patcher-Program.cs` (new).
- `releases/11.0.1267/install-msix.sh`: .NET desktop runtime pin moved to 10.0.14, matching
  11.0.1389's runtimeconfig.
- `original/em-11.0.1389/` and `original/em-11.0.1389-x64/` reference snapshots (2549/2552 files,
  same layout as earlier builds).

## Notes / remaining risk

- eM Client updates itself (the 1267 install here auto-updated to 1389 within the hour). An update
  replaces patched assemblies, so re-run `deploy.sh` after any update; the marker in
  `MailClient.Wine.dll` reports the stage mask, but a new build should get a fresh full pass.
- A live click of the **Activate** button after deploying has not been recorded in this session.
  The cryptographic verification above is exercised on the exact token and exact patched assembly,
  so the previously failing step now succeeds; the remaining check is simply that the UI accepts
  it.
