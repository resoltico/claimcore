module ClaimCore.Tests.CompartmentTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private valid result =
    Result.defaultWith (fun problem -> failtestf "Compartment fixture: %A" problem) result

let private installation = Guid.Parse "00000000-0000-4000-8000-000000000001"

let private operation number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)
    |> OperationId.create
    |> Option.get

let private owner number =
    Guid.Parse(sprintf "00000000-0000-7000-8000-%012d" number)
    |> CaseId.create
    |> Option.get
    |> CompartmentOwner.Case

let private nonce () = [| 0uy .. 31uy |]

let private payload () =
    Encoding.UTF8.GetBytes """{"claimant":"Ā 😀"}"""

let private descriptor =
    CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Case [ 0 ] |> valid

let private seal () =
    Compartment.seal installation (operation 2) descriptor (nonce ()) (payload ())
    |> valid

// Independent byte derivation, before the product encoder: u32 frame length, UTF-8 tag,
// u32 format=2, RFC4122 installation/operation, owner-kind/UUID, purpose, ordinal,
// reference count/reference, exactly 32 nonce bytes, then u32 UTF-8 payload length/payload.
// Python stdlib struct/uuid/hashlib independently gave 149 bytes and the literal digest below.
let private preimage =
    String.concat
        ""
        [
            "00000015"
            "436c61696d436f72652e436f6d706172746d656e74"
            "00000002"
            "00000000000040008000000000000001"
            "00000000000070008000000000000002"
            "01"
            "00000000000070008000000000000003"
            "01"
            "00000000"
            "00000001"
            "00000000"
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
            "00000016"
            "7b22636c61696d616e74223a22c48020f09f9880227d"
        ]
    |> Convert.FromHexString

let private digest =
    Convert.FromHexString "ebc234ab1404b683d936f542b1929302640ed31f929c82eda6be871a2b592540"

[<Tests>]
let tests =
    testList
        "owner compartment commitments"
        [
            testCase
                "hand-authored framed Unicode bytes independently derive the commitment"
                (fun () ->
                    Expect.equal preimage.Length 149 "Independently counted framing"
                    Expect.equal (SHA256.HashData preimage) digest "Independent hash derivation"
                    let actual = seal ()

                    Expect.equal
                        actual.Commitment
                        digest
                        "Product framing matches the external derivation"

                    Expect.equal
                        (Compartment.content actual)
                        (Some(nonce (), payload ()))
                        "Available canonical bytes and nonce retained")
            testCase
                "owner purpose ordinal references operation installation nonce and contents are bound"
                (fun () ->
                    let original = seal ()

                    let restore installation operation descriptor nonce payload =
                        Compartment.restore
                            installation
                            operation
                            descriptor
                            original.Commitment
                            (Some nonce)
                            (Some payload)

                    let differentNonce = nonce ()
                    differentNonce[0] <- 32uy

                    let changedDescriptors =
                        [
                            CompartmentDescriptor.create 0 (owner 4) CompartmentPurpose.Case [ 0 ]
                            CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Work [ 0 ]
                            CompartmentDescriptor.create 1 (owner 3) CompartmentPurpose.Case [ 0 ]
                            CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Case [ 1 ]
                        ]
                        |> List.map valid

                    for changed in changedDescriptors do
                        Expect.equal
                            (restore installation (operation 2) changed (nonce ()) (payload ()))
                            (Error CompartmentProblem.CommitmentMismatch)
                            "Descriptor reassignment changes the commitment"

                    for changed in
                        [
                            restore
                                (Guid.Parse "00000000-0000-4000-8000-000000000004")
                                (operation 2)
                                descriptor
                                (nonce ())
                                (payload ())
                            restore installation (operation 4) descriptor (nonce ()) (payload ())
                            restore
                                installation
                                (operation 2)
                                descriptor
                                differentNonce
                                (payload ())
                            restore
                                installation
                                (operation 2)
                                descriptor
                                (nonce ())
                                (Encoding.UTF8.GetBytes """{"claimant":"B 😀"}""")
                        ] do
                        Expect.equal
                            changed
                            (Error CompartmentProblem.CommitmentMismatch)
                            "Root binding and exact content are checked")
            testCase "erasure preserves binding and removes bytes together with nonce" (fun () ->
                let original = seal ()
                let erased = Compartment.withoutContent original
                Expect.isNone (Compartment.content erased) "No private bytes or nonce remain"

                Expect.equal
                    erased.Descriptor
                    original.Descriptor
                    "Owner and references remain immutable"

                Expect.equal erased.Commitment digest "Blinded commitment remains"

                Expect.equal
                    (Compartment.restore installation (operation 2) descriptor digest None None)
                    (Ok erased)
                    "Erased interpretation makes no claim about unavailable content"

                for nonce, content in [ None, Some(payload ()); Some(nonce ()), None ] do
                    Expect.equal
                        (Compartment.restore
                            installation
                            (operation 2)
                            descriptor
                            digest
                            nonce
                            content)
                        (Error CompartmentProblem.IncompleteContent)
                        "Half-clearing is corruption")
            testCase "nonce duplication checks only available supplied compartments" (fun () ->
                let first = seal ()

                let second =
                    Compartment.seal installation (operation 4) descriptor (nonce ()) (payload ())
                    |> valid

                Expect.isFalse
                    (Compartment.distinctAvailableNonces [ first; second ])
                    "Duplicate CSPRNG output is rejected within available scope"

                Expect.isTrue
                    (Compartment.distinctAvailableNonces
                        [ Compartment.withoutContent first; second ])
                    "Erased nonce freshness depends on the trusted generator, not recoverable evidence"

                let distinct = nonce ()
                distinct[31] <- 32uy

                let third =
                    Compartment.seal installation (operation 4) descriptor distinct (payload ())
                    |> valid

                Expect.isTrue
                    (Compartment.distinctAvailableNonces [ first; third ])
                    "Independent available nonces remain distinguishable")
            testCase
                "callers cannot mutate retained content or its commitment through arrays"
                (fun () ->
                    let suppliedNonce, suppliedBytes = nonce (), payload ()

                    let value =
                        Compartment.seal
                            installation
                            (operation 2)
                            descriptor
                            suppliedNonce
                            suppliedBytes
                        |> valid

                    suppliedNonce[0] <- 255uy
                    suppliedBytes[0] <- 0uy
                    let returnedNonce, returnedBytes = Compartment.content value |> Option.get
                    returnedNonce[0] <- 255uy
                    returnedBytes[0] <- 0uy
                    let commitment = value.Commitment
                    commitment[0] <- 0uy

                    Expect.equal
                        value.Commitment
                        digest
                        "Commitment storage does not alias callers"

                    Expect.equal
                        (Compartment.content value)
                        (Some(nonce (), payload ()))
                        "Payload and nonce storage do not alias callers")
            testCase "descriptor and content boundaries refuse invalid structure" (fun () ->
                Expect.equal
                    (CompartmentDescriptor.create -1 (owner 3) CompartmentPurpose.Case [ 0 ])
                    (Error CompartmentProblem.InvalidOrdinal)
                    "Ordinal cannot be negative"

                Expect.equal
                    (CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Profile [ 0 ])
                    (Error CompartmentProblem.InvalidOwnerPurpose)
                    "A profile is never case-owned"

                for refs in [ []; [ -1 ]; [ 0; 0 ]; [ 1; 0 ] ] do
                    Expect.equal
                        (CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Reminder refs)
                        (Error CompartmentProblem.InvalidEffectReferences)
                        "References are ordered, nonempty and unique"

                Expect.isError
                    (CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Case [ 0; 1 ])
                    "Single-effect purpose cannot acquire extra references"

                Expect.isOk
                    (CompartmentDescriptor.create 0 (owner 3) CompartmentPurpose.Reminder [ 0; 1 ])
                    "Compound reminder can bind both effects"

                Expect.equal
                    (Compartment.seal Guid.Empty (operation 2) descriptor (nonce ()) (payload ()))
                    (Error CompartmentProblem.InvalidInstallation)
                    "Installation scope is mandatory"

                Expect.equal
                    (Compartment.seal
                        installation
                        (operation 2)
                        descriptor
                        (Array.zeroCreate 31)
                        (payload ()))
                    (Error CompartmentProblem.InvalidNonce)
                    "Nonce width is fixed"

                for bytes in
                    [ Array.empty; Array.zeroCreate (Compartment.MaximumContentBytes + 1) ] do
                    Expect.equal
                        (Compartment.seal installation (operation 2) descriptor (nonce ()) bytes)
                        (Error CompartmentProblem.InvalidContent)
                        "Opaque compartment content is bounded"

                Expect.equal
                    (Compartment.restore
                        installation
                        (operation 2)
                        descriptor
                        (Array.zeroCreate 31)
                        None
                        None)
                    (Error CompartmentProblem.InvalidCommitment)
                    "Retained blinded commitment has fixed width")
        ]
