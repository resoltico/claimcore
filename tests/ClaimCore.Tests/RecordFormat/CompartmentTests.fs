module ClaimCore.Tests.CompartmentTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat

let private valid result =
    result
    |> Result.defaultWith (fun problem -> failtestf "Compartment fixture: %A" problem)

let private owner number =
    Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" number)
    |> CaseId.create
    |> Option.get
    |> CompartmentOwner.Family

let private descriptor number =
    CompartmentDescriptor.create 0 (owner number) |> valid

let private nonce () = [| 0uy .. 31uy |]

let private payload () =
    Encoding.UTF8.GetBytes """{"claimant":"Ā 😀"}"""

let private seal () =
    Compartment.seal (descriptor 3) (nonce ()) (payload ()) |> valid

[<Tests>]
let tests =
    testList
        "owned private compartments"
        [
            testCase
                "format 5 commitment is the independently derived nonce and payload digest"
                (fun () ->
                    // Python hashlib over the hand-authored 32-byte nonce followed by 22 UTF-8 bytes.
                    let expected =
                        Convert.FromHexString
                            "0e701c1ae12131fcb436ce7609f0249f135f8033048ef770c9a5bb4b7eb72a69"

                    let value = seal ()
                    Expect.equal value.Commitment expected "Literal SHA-256 vector"

                    Expect.isOk
                        (Compartment.restore
                            value.Descriptor
                            expected
                            (Some(nonce ()))
                            (Some(payload ())))
                        "Available content restores")
            testCase
                "nonce, content and commitment corruption are independently rejected"
                (fun () ->
                    let value = seal ()
                    let changedNonce = nonce ()
                    changedNonce[0] <- changedNonce[0] ^^^ 1uy

                    Expect.equal
                        (Compartment.restore
                            value.Descriptor
                            value.Commitment
                            (Some changedNonce)
                            (Some(payload ())))
                        (Error CompartmentProblem.CommitmentMismatch)
                        "Nonce mutation"

                    let bytes = payload ()
                    bytes[0] <- bytes[0] ^^^ 1uy

                    Expect.equal
                        (Compartment.restore
                            value.Descriptor
                            value.Commitment
                            (Some(nonce ()))
                            (Some bytes))
                        (Error CompartmentProblem.CommitmentMismatch)
                        "Payload mutation"

                    let digest = value.Commitment
                    digest[0] <- digest[0] ^^^ 1uy

                    Expect.equal
                        (Compartment.restore
                            value.Descriptor
                            digest
                            (Some(nonce ()))
                            (Some(payload ())))
                        (Error CompartmentProblem.CommitmentMismatch)
                        "Commitment mutation")
            testCase
                "erasure retains descriptor and commitment while removing nonce and payload together"
                (fun () ->
                    let available = seal ()
                    let erased = Compartment.withoutContent available
                    Expect.equal erased.Descriptor available.Descriptor "Owner and ordinal remain"

                    Expect.equal
                        erased.Commitment
                        available.Commitment
                        "Blinded commitment remains"

                    Expect.isNone (Compartment.content erased) "Nonce and payload are unavailable"

                    Expect.isOk
                        (Compartment.restore erased.Descriptor erased.Commitment None None)
                        "Stored erasure restores"

                    Expect.equal
                        (Compartment.restore
                            erased.Descriptor
                            erased.Commitment
                            (Some(nonce ()))
                            None)
                        (Error CompartmentProblem.IncompleteContent)
                        "Nonce alone is not content"

                    Expect.equal
                        (Compartment.restore
                            erased.Descriptor
                            erased.Commitment
                            None
                            (Some(payload ())))
                        (Error CompartmentProblem.IncompleteContent)
                        "Payload alone is not content")
            testCase
                "nonce uniqueness considers only the supplied available compartments"
                (fun () ->
                    let a = seal ()

                    let b =
                        Compartment.seal
                            (descriptor 4)
                            (nonce ())
                            (Encoding.UTF8.GetBytes "different")
                        |> valid

                    Expect.isFalse
                        (Compartment.distinctAvailableNonces [ a; b ])
                        "Another owner cannot reuse an available nonce"

                    Expect.isTrue
                        (Compartment.distinctAvailableNonces [ Compartment.withoutContent a; b ])
                        "Erased nonce is not fabricated"

                    Expect.isTrue
                        (Compartment.distinctAvailableNonces
                            [ Compartment.withoutContent a; Compartment.withoutContent b ])
                        "Erased inputs remain explicit")
            testCase
                "all incoming and returned arrays are protected against alias mutation"
                (fun () ->
                    let rawNonce, rawPayload = nonce (), payload ()
                    let value = Compartment.seal (descriptor 3) rawNonce rawPayload |> valid
                    rawNonce[0] <- 255uy
                    rawPayload[0] <- 255uy
                    let returnedNonce, returnedPayload = Compartment.content value |> Option.get
                    Expect.equal returnedNonce (nonce ()) "Input nonce was copied"
                    Expect.equal returnedPayload (payload ()) "Input payload was copied"
                    returnedNonce[0] <- 255uy
                    returnedPayload[0] <- 255uy
                    let commitment = value.Commitment
                    commitment[0] <- 255uy
                    let restoredNonce, restoredPayload = Compartment.content value |> Option.get
                    Expect.equal restoredNonce (nonce ()) "Returned nonce is a copy"
                    Expect.equal restoredPayload (payload ()) "Returned payload is a copy"

                    Expect.equal
                        (value.Commitment |> Convert.ToHexStringLower)
                        "0e701c1ae12131fcb436ce7609f0249f135f8033048ef770c9a5bb4b7eb72a69"
                        "Returned commitment is a copy")
            testCase "descriptor, nonce, content and digest boundaries are enforced" (fun () ->
                for ordinal in [ -1; 2 ] do
                    Expect.equal
                        (CompartmentDescriptor.create ordinal (owner 3))
                        (Error CompartmentProblem.InvalidOrdinal)
                        "Only the declared ordinal range is representable"

                for nonce in
                    [
                        Unchecked.defaultof<byte array>
                        [||]
                        Array.zeroCreate 31
                        Array.zeroCreate 33
                    ] do
                    Expect.equal
                        (Compartment.seal (descriptor 3) nonce (payload ()))
                        (Error CompartmentProblem.InvalidNonce)
                        "Fixed nonce width"

                for bytes in
                    [
                        Unchecked.defaultof<byte array>
                        [||]
                        Array.zeroCreate (Compartment.MaximumContentBytes + 1)
                    ] do
                    Expect.equal
                        (Compartment.seal (descriptor 3) (nonce ()) bytes)
                        (Error CompartmentProblem.InvalidContent)
                        "Content bounds"

                Expect.isOk
                    (Compartment.seal
                        (descriptor 3)
                        (nonce ())
                        (Array.zeroCreate Compartment.MaximumContentBytes))
                    "Exact content ceiling"

                Expect.equal
                    (Compartment.restore (descriptor 3) (Array.zeroCreate 31) None None)
                    (Error CompartmentProblem.InvalidCommitment)
                    "Fixed retained digest width")
        ]
