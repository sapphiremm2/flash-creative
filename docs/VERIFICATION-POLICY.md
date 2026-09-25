# Verification policy

Decision accepted by the project owner on 2026-09-25: defer research into Adobe's
opaque detached `PackageValidation` signatures. It is not an installation-development
blocker. Retain the field and report `DeferredUnverified` / `DetachedSignatureVerified=false`.
Do not claim the field is verified or silently replace it with a successful hash check.

## Required checks

- Official Adobe HTTPS URLs, normal TLS certificate validation, and validation of each redirect.
- Fresh Adobe SHA-256 segment metadata for package downloads and installation/staging input.
  A locally supplied digest or receipt alone is insufficient to authorize installation.
- Exact package identity, supported instructions, bounded decoding, and safe path handling.
- Before any executable installer is launched, valid Windows Authenticode trust and the
  expected publisher from an explicit allowlist. Verification must be bound to the file
  actually executed; publisher identity supplied by downloaded metadata is insufficient.
  The standalone verifier exists; the execution boundary is still unimplemented.
- Preserve Adobe sign-in/licensing and subscription requirements.

These checks do not require installing the Creative Cloud desktop app or background services.
They do not materially determine installed application size; component selection does.
HTTPS hashes depend on the same source as the payload, so they do not independently
protect against that source serving altered payloads and matching metadata. Authenticode
adds publisher verification for signed executables, not arbitrary ZIP contents, manifests,
or scripts. This is an explicit limitation of the chosen trust model.

Offline receipt audits remain diagnostic only. Offline installation authorization is not
implemented. Unknown operations, signature failures, or hash mismatches must not gain an
"ignore validation" path because detached Adobe signature research was deferred.

Reference: [Microsoft WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust).
