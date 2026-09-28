module ClaimCore.IntegrationTests.ManagedCopyCryptoTests

open System
open Expecto
open ClaimCore.Postgres

let private rfc8032Vector =
    testCase
        "[CC-BACKUP-001] registered Ed25519 verifier accepts RFC 8032 vector and rejects tampering"
        (fun _ ->
            let publicKey =
                Convert.FromHexString(
                    "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c"
                )

            let message = [| 0x72uy |]

            let signature =
                Convert.FromHexString(
                    "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da"
                    + "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
                )

            Expect.isTrue
                (ManagedCopySignature.verify publicKey message signature)
                "Exact RFC 8032 Ed25519 signature verifies."

            Expect.isFalse
                (ManagedCopySignature.verify publicKey [| 0x73uy |] signature)
                "Changed bytes cannot verify."

            signature[0] <- signature[0] ^^^ 1uy

            Expect.isFalse
                (ManagedCopySignature.verify publicKey message signature)
                "A changed detached signature cannot verify."

            Expect.isFalse
                (ManagedCopySignature.verify Array.empty message signature)
                "An incoming copy cannot supply an invalid public key.")

let tests = testList "managed-copy cryptography" [ rfc8032Vector ]
