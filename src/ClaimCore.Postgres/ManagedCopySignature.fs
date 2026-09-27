namespace ClaimCore.Postgres

open NSec.Cryptography

/// Registered raw Ed25519 public keys only. Incoming copy bytes never supply a trust anchor.
module internal ManagedCopySignature =
    let private import (publicKey: byte array) =
        if isNull (box publicKey) || publicKey.Length <> 32 then
            invalidArg (nameof publicKey) "A raw Ed25519 public key is required."

        PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey)

    let validPublicKey publicKey =
        try
            let imported = import publicKey
            not (isNull (box imported))
        with _ ->
            false

    let verify publicKey (canonical: byte array) (signature: byte array) =
        if
            isNull (box canonical)
            || isNull (box signature)
            || canonical.Length = 0
            || canonical.Length > 300000
            || signature.Length <> 64
        then
            false
        else
            try
                let imported = import publicKey
                SignatureAlgorithm.Ed25519.Verify(imported, canonical, signature)
            with _ ->
                false
