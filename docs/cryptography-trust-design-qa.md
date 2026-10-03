# Cryptography and trust design challenge

This separate pass tests the proposed boundaries against plausible failure sequences.

- Npgsql `VerifyFull` checks name and chain, but its revocation flag defaults false. Setting the
  flag only in a helper used by some owner commands is insufficient: direct owner suboperations
  receive the same raw private connection text. Normalize once before routing commands, and test
  that an unsafe remote string opens no connection and yields the existing safe owner refusal.
  Apply the same remote rule to all three witness-private connection inputs. Loopback development
  remains permitted without a CRL; remote missing/unknown revocation data may refuse service.
- An IdP startup metadata check with revocation enabled is insufficient if runtime discovery,
  token and JWKS fetches use default handlers. Set every production backchannel. Keep redirected
  secret-bearing POSTs out of Web's backchannel, and exercise the real published OIDC/CLI fixture.
- Rebuilding a custom chain without an application policy can accept a client-authentication leaf.
  The trusted root must be a CA, not a leaf used as its own trust anchor. The existing CLI signal
  fixture currently uses such a leaf: replace it with separate CA and server keys. Require both
  negative and positive TLS controls; avoid accepting CRL-less custom roots on non-loopback hosts.
- Checking only key IDs cannot distinguish a rotation that reused key bytes. Compare material
  while the bounded parser holds it, erase temporary copies and retain all historical decryptors.
  Reject zero bytes as an obvious defect without claiming that nonzero bytes prove entropy.
  A runtime refusal must not delete or rewrite an existing installation; operators retain old
  software/evidence to repair an incorrectly reused historical key under owner procedure.
- A removed issuer signing key may remain in framework metadata caches, and an existing browser
  ticket remains server-side. TLS revocation cannot close an already established session. The
  supported emergency response is host/session shutdown plus authoritative grant revocation and
  exact audit; no automatic key-file polling, silent replay, or false immediate-revocation claim.
- AES-GCM tag failure already clears plaintext in .NET, and current recovery import additionally
  zeroes its payload. Do not add duplicate erasure scaffolding. Witness hash/sequence checks and
  primary receipt correlation remain mandatory even with authenticated ciphertext.

Accepted with these constraints. Keep all test data synthetic, verify the final published bytes
and complete suites, inspect the actual PR diff and protections, then merge as explicitly requested.
