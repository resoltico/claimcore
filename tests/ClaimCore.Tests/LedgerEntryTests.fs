module ClaimCore.Tests.LedgerEntryTests

open ClaimCore.Contract

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private text (element: JsonElement) (name: string) =
    element.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Missing vector text")

let private scope (vector: JsonElement) =
    if text vector "scope" = "AUTHORITY" then
        LedgerScope.Authority
            {
                TargetActorId = Guid.Parse(text vector "subjectId")
                Act = AuthorityKind.SetGrant
            }
    else
        LedgerScope.Case
            {
                CaseId = Guid.Parse(text vector "subjectId")
                Revision = vector.GetProperty("caseRevision").GetInt64()
                Act = (CaseActKind.Business BusinessActKind.Open)
                BusinessDate =
                    DateOnly.Parse(text vector "businessDate", CultureInfo.InvariantCulture)
            }

let private metadata vector : LedgerMetadata =
    {
        InstallationId = Guid.Parse(text vector "installationId")
        Sequence = vector.GetProperty("sequence").GetInt64()
        PreviousHash = Convert.FromHexString(text vector "previousHash")
        OperationId = Guid.Parse(text vector "operationId")
        ActorId = Guid.Parse(text vector "actorId")
        RecordedAt = DateTimeOffset.Parse(text vector "recordedAt", CultureInfo.InvariantCulture)
        Scope = scope vector
        PayloadCommitment = Convert.FromHexString(text vector "payloadCommitment")
    }

let private caseMetadata (entry: LedgerMetadata) =
    match entry.Scope with
    | LedgerScope.Case value -> value
    | _ -> failtest "Case seed required"

// These independent encoding vectors are not provisioned authority histories.
let private withVectors action =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(RepositoryRoot.find (), "tests/fixtures/record-format-vectors.json")
            )
        )

    action (
        document.RootElement.GetProperty("ledgerEntries").GetProperty("vectors").EnumerateArray()
        |> Seq.toList
    )

let private expectedPayload vector =
    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/record-format-vectors.json")

    use document = JsonDocument.Parse(File.ReadAllBytes path)
    let payloads = document.RootElement.GetProperty "ledgerPayloads"

    let request, snapshot =
        if text vector "scope" = "CASE" then
            text payloads "caseRequest", text payloads "caseSnapshot" |> Encoding.UTF8.GetBytes
        else
            text payloads "authorityRequest", Array.empty

    {
        Request = Encoding.UTF8.GetBytes request
        Snapshot = snapshot
    }

let private vectors () =
    withVectors (fun vectors ->
        Expect.equal vectors.Length 2 "All independent vectors"

        for vector in vectors do
            let payload = expectedPayload vector

            let encoded = LedgerEntry.encodePayload payload
            let nonce = Convert.FromHexString(text vector "payloadNonce")
            let entry = metadata vector

            Expect.equal
                (Convert.ToHexStringLower encoded)
                (text vector "payload")
                "Independent framing"

            Expect.equal (LedgerEntry.decodePayload 262144 encoded) (Ok payload) "Payload meaning"

            Expect.equal
                (LedgerEntry.commitment nonce encoded)
                entry.PayloadCommitment
                "Independent commitment"

            Expect.equal
                (Convert.ToHexStringLower(LedgerEntry.preimage entry))
                (text vector "preimage")
                "All 183 fixed bytes"

            Expect.equal
                (Convert.ToHexStringLower(LedgerEntry.hash entry))
                (text vector "entryHash")
                "Independent hash")

let private changedHash (value: byte array) =
    let copy = Array.copy value
    copy[0] <- copy[0] ^^^ 1uy
    copy

let private changedCaseMetadata otherId case =
    [
        { case with CaseId = otherId }
        { case with Revision = 2L }
        { case with
            Act = (CaseActKind.Business BusinessActKind.Close)
        }
        { case with
            BusinessDate = case.BusinessDate.AddDays 1
        }
    ]


let private allMetadataFields () =
    withVectors (fun vectors ->
        let entry = metadata vectors.Head

        let case = caseMetadata entry

        let otherId = Guid.Parse "40000000-0000-4000-8000-000000000004"

        let changes =
            [
                { entry with InstallationId = otherId }
                { entry with Sequence = 2L }
                { entry with
                    PreviousHash = changedHash entry.PreviousHash
                }
                { entry with OperationId = otherId }
                { entry with ActorId = otherId }
                { entry with
                    RecordedAt = entry.RecordedAt.AddTicks 10L
                }
                { entry with
                    PayloadCommitment = changedHash entry.PayloadCommitment
                }
                { entry with
                    Scope =
                        LedgerScope.Authority
                            {
                                TargetActorId = otherId
                                Act = AuthorityKind.SetGrant
                            }
                }
            ]

        let scopes = changedCaseMetadata otherId case

        let original = LedgerEntry.hash entry

        for candidate in
            changes
            @ (scopes
               |> List.map (fun value ->
                   { entry with
                       Scope = LedgerScope.Case value
                   })) do
            Expect.isTrue
                (LedgerEntry.hash candidate <> original)
                "Every fixed metadata field is bound")

let private framingProperty =
    property {
        let! request = DecoderCorpora.arbitraryBytes
        let! snapshot = DecoderCorpora.arbitraryBytes

        let payload =
            {
                Request = request
                Snapshot = snapshot
            }

        return LedgerEntry.decodePayload 65536 (LedgerEntry.encodePayload payload) = Ok payload
    }

let private hashProperty =
    property {
        let! sequence = Gen.int64 (Range.constant 1L Revision.Maximum)
        let! index = Gen.int32 (Range.constant 0 31)

        return
            withVectors (fun vectors ->
                let entry =
                    { metadata vectors.Head with
                        Sequence = sequence
                    }

                let commitment = Array.copy entry.PayloadCommitment
                commitment[index] <- commitment[index] ^^^ 1uy

                LedgerEntry.hash
                    { entry with
                        PayloadCommitment = commitment
                    }
                <> LedgerEntry.hash entry)
    }

[<Tests>]
let tests =
    testList
        "ledger entry format"
        [
            testCase "both independent ledger vectors reproduce every byte" vectors
            testCase "each fixed metadata field affects the hash" allMetadataFields
            testCase "act codes have an independent append-only expectation" (fun () ->
                let acts =
                    [
                        (CaseActKind.Business BusinessActKind.Open)
                        (CaseActKind.Business BusinessActKind.AmendRegistration)
                        (CaseActKind.Business BusinessActKind.CorrectCase)
                        (CaseActKind.Business BusinessActKind.Decide)
                        (CaseActKind.Business BusinessActKind.WithdrawDecision)
                        (CaseActKind.Business BusinessActKind.RecordPayment)
                        (CaseActKind.Business BusinessActKind.ClearPayment)
                        (CaseActKind.Business BusinessActKind.Close)
                        (CaseActKind.Business BusinessActKind.Reopen)
                        (CaseActKind.Stewardship StewardshipActKind.Void)
                        (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                        (CaseActKind.Stewardship StewardshipActKind.Erase)
                    ]

                Expect.equal
                    (List.map LedgerEntry.actCode acts)
                    [ 1uy .. 12uy ]
                    "Durable act codes")
            testCase "all authority act codes and subjects are hash-bound" (fun () ->
                withVectors (fun vectors ->
                    let entry = metadata vectors[1]

                    for kind, expected in
                        [
                            AuthorityKind.ProvisionOwner, 32uy
                            AuthorityKind.RegisterActor, 33uy
                            AuthorityKind.SetGrant, 34uy
                            AuthorityKind.SetEnabled, 35uy
                        ] do
                        let scope =
                            {
                                TargetActorId = entry.ActorId
                                Act = kind
                            }

                        let candidate =
                            { entry with
                                Scope = LedgerScope.Authority scope
                            }

                        Expect.equal
                            ((LedgerEntry.preimage candidate)[82])
                            expected
                            "Independent authority code"

                        let changed =
                            { candidate with
                                Scope =
                                    LedgerScope.Authority
                                        { scope with
                                            TargetActorId = entry.OperationId
                                        }
                            }

                        Expect.isTrue
                            (LedgerEntry.hash changed <> LedgerEntry.hash candidate)
                            "Target actor is hash-bound"

                    let invalid =
                        { entry with
                            Scope =
                                LedgerScope.Authority
                                    {
                                        TargetActorId = Guid.Empty
                                        Act = AuthorityKind.SetGrant
                                    }
                        }

                    Expect.throwsT<ArgumentException>
                        (fun () -> LedgerEntry.hash invalid |> ignore)
                        "Subject required"))
            testCase "payload framing round trips arbitrary bytes" (fun () ->
                PropertyHarness.run "CC-PROP-LEDGER-PAYLOAD-001" framingProperty)
            testCase "commitment changes affect hashes across the sequence range" (fun () ->
                PropertyHarness.run "CC-PROP-LEDGER-HASH-001" hashProperty)
            testCase "erasure leaves recomputation independent of payload and nonce" (fun () ->
                withVectors (fun vectors ->
                    for vector in vectors do
                        let entry = metadata vector
                        let hash = LedgerEntry.hash entry
                        let payload = text vector "payload" |> Convert.FromHexString
                        let nonce = text vector "payloadNonce" |> Convert.FromHexString

                        Array.Clear payload
                        Array.Clear nonce

                        Expect.equal
                            (LedgerEntry.hash entry)
                            hash
                            "Only retained metadata is required"

                        Expect.equal
                            (Convert.ToHexStringLower hash)
                            (text vector "entryHash")
                            "Independent retained position"))
            testCase "framing rejects truncation trailing bytes overflow and limits" (fun () ->
                let valid =
                    LedgerEntry.encodePayload
                        {
                            Request = [| 1uy |]
                            Snapshot = [| 2uy |]
                        }

                for bytes in
                    [
                        valid[.. valid.Length - 2]
                        Array.append valid [| 0uy |]
                        [| 255uy; 255uy; 255uy; 255uy; 0uy; 0uy; 0uy; 0uy |]
                        [| 0uy; 0uy; 0uy; 0uy; 255uy; 255uy; 255uy; 255uy |]
                    ] do
                    Expect.isError (LedgerEntry.decodePayload 262144 bytes) "Malformed framing"

                Expect.isError (LedgerEntry.decodePayload (valid.Length - 1) valid) "Over limit"
                Expect.isOk (LedgerEntry.decodePayload valid.Length valid) "Exact limit")
            testCase
                "metadata guards reject invalid widths identities and sub-microsecond instants"
                (fun () ->
                    withVectors (fun vectors ->
                        let entry = metadata vectors.Head

                        let cases =
                            [
                                { entry with PreviousHash = [||] }
                                { entry with PayloadCommitment = [||] }
                                { entry with OperationId = Guid.Empty }
                                { entry with
                                    InstallationId = Guid.Empty
                                }
                                { entry with ActorId = Guid.Empty }
                                { entry with Sequence = 0L }
                                { entry with Sequence = -1L }
                                { entry with
                                    Sequence = Revision.Maximum + 1L
                                }
                                { entry with
                                    RecordedAt = entry.RecordedAt.AddTicks 1L
                                }
                            ]

                        for sequence in [ 1L; Revision.Maximum - 1L; Revision.Maximum ] do
                            Expect.equal
                                (LedgerEntry.hash { entry with Sequence = sequence }).Length
                                32
                                "Safe boundary is representable"

                        for invalid in cases do
                            Expect.throwsT<ArgumentException>
                                (fun () -> LedgerEntry.hash invalid |> ignore)
                                "Invalid metadata refused"

                        Expect.throwsT<ArgumentException>
                            (fun () -> LedgerEntry.commitment [||] [||] |> ignore)
                            "Nonce width refused"))
            testCase "case metadata guards reject invalid identifiers and revisions" (fun () ->
                withVectors (fun vectors ->
                    let entry = metadata vectors.Head

                    let case = caseMetadata entry

                    for invalid in
                        [
                            { case with CaseId = Guid.Empty }
                            { case with Revision = 0L }
                            { case with Revision = Int64.MaxValue }
                            { case with
                                Revision = Revision.Maximum + 1L
                            }
                        ] do
                        Expect.throwsT<ArgumentException>
                            (fun () ->
                                LedgerEntry.hash
                                    { entry with
                                        Scope = LedgerScope.Case invalid
                                    }
                                |> ignore)
                            "Invalid case metadata refused"))
            testCase "the signed instant is UTC and retains pre-epoch microseconds" (fun () ->
                withVectors (fun vectors ->
                    let entry = metadata vectors.Head

                    let beforeEpoch =
                        { entry with
                            RecordedAt = DateTimeOffset.UnixEpoch.AddTicks(-10L)
                        }

                    let bytes = LedgerEntry.preimage beforeEpoch

                    Expect.equal
                        (Convert.ToHexStringLower bytes[115..122])
                        "ffffffffffffffff"
                        "Signed microsecond minus one"

                    Expect.equal
                        (LedgerEntry.hash
                            { entry with
                                RecordedAt = entry.RecordedAt.ToOffset(TimeSpan.FromHours 3)
                            })
                        (LedgerEntry.hash entry)
                        "The instant is independent of offset spelling"))
            testCase "the commitment binds both nonce and payload" (fun () ->
                let nonce = [| 0uy .. 31uy |]
                let payload = [| 1uy; 2uy; 3uy |]
                let original = LedgerEntry.commitment nonce payload

                Expect.isTrue
                    (original <> LedgerEntry.commitment (changedHash nonce) payload)
                    "Nonce is required"

                Expect.isTrue
                    (original <> LedgerEntry.commitment nonce [| 1uy; 2uy; 4uy |])
                    "Payload is required")
            testCase
                "day zero remains a case date and cannot identify an authority entry"
                (fun () ->
                    withVectors (fun vectors ->
                        let entry = metadata vectors.Head

                        let case = caseMetadata entry

                        let caseBytes =
                            LedgerEntry.preimage
                                { entry with
                                    Scope =
                                        LedgerScope.Case
                                            { case with
                                                BusinessDate = DateOnly.MinValue
                                            }
                                }

                        let authorityBytes =
                            LedgerEntry.preimage
                                { entry with
                                    Scope =
                                        LedgerScope.Authority
                                            {
                                                TargetActorId = entry.ActorId
                                                Act = AuthorityKind.SetGrant
                                            }
                                }

                        Expect.equal caseBytes[81] 1uy "Case kind"
                        Expect.equal authorityBytes[81] 2uy "Authority kind"
                        Expect.equal caseBytes[147..150] [| 0uy; 0uy; 0uy; 0uy |] "Real day zero"))
        ]
