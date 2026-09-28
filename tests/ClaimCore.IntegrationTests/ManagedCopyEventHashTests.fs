module ClaimCore.IntegrationTests.ManagedCopyEventHashTests

open System
open System.Text
open Expecto
open ClaimCore.Postgres

let private exactEvidence () =
    let previous = Array.zeroCreate<byte> 32
    let canonical = Encoding.ASCII.GetBytes("{\"copyId\":\"synthetic\"}\n")
    let first = ManagedCopyEventHash.compute previous canonical None
    let repeated = ManagedCopyEventHash.compute previous canonical None
    Expect.sequenceEqual first repeated "Exact event evidence has a stable hash."
    let changed = Encoding.ASCII.GetBytes("{\"copyId\":\"different\"}\n")

    Expect.notEqual
        first
        (ManagedCopyEventHash.compute previous changed None)
        "Exact attestation bytes are bound."

    let signature = Array.create 64 1uy

    Expect.notEqual
        first
        (ManagedCopyEventHash.compute previous canonical (Some signature))
        "Detached signature bytes are bound."

    let later = Array.create 32 2uy

    Expect.notEqual
        first
        (ManagedCopyEventHash.compute later canonical None)
        "Per-copy predecessor hash is bound."

let private invalidEvidence () =
    let previous = Array.zeroCreate<byte> 32
    let canonical = Encoding.ASCII.GetBytes("synthetic")

    Expect.throwsT<ArgumentException>
        (fun () -> ManagedCopyEventHash.compute Array.empty canonical None |> ignore)
        "A missing predecessor hash is refused."

    Expect.throwsT<ArgumentException>
        (fun () ->
            ManagedCopyEventHash.compute previous canonical (Some(Array.zeroCreate 63))
            |> ignore)
        "A non-Ed25519 detached signature is refused."

let tests =
    testList
        "managed-copy event hash"
        [
            testCase
                "[CC-BACKUP-001] managed-copy hash binds exact bytes and signature"
                exactEvidence
            testCase "[CC-BACKUP-001] managed-copy hash rejects malformed evidence" invalidEvidence
        ]
