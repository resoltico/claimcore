module ClaimCore.Tests.LedgerEntryTests

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
    if text vector "kind" = "AUTHORITY" then
        LedgerScope.Authority
    else
        let act =
            match text vector "commandKind" with
            | "OPEN" -> LedgerAct.Command CommandKind.Open
            | "ERASE" -> LedgerAct.Stewardship StewardshipKind.Erase
            | _ -> failtest "Unexpected vector act"

        LedgerScope.Case
            {
                CaseId = Guid.Parse(text vector "caseId")
                Revision = vector.GetProperty("caseRevision").GetInt64()
                Act = act
                BusinessDate =
                    DateOnly.Parse(text vector "businessDate", CultureInfo.InvariantCulture)
                RuleRevision = vector.GetProperty("ruleRevision").GetUInt16()
            }

let private metadata vector : LedgerMetadata =
    {
        InstallationId = Guid.Parse(text vector "installationId")
        Sequence = vector.GetProperty("sequence").GetUInt64()
        PreviousHash = Convert.FromHexString(text vector "previousHash")
        OperationId = Guid.Parse(text vector "operationId")
        ActorId = Guid.Parse(text vector "actorId")
        RecordedAt = DateTimeOffset.Parse(text vector "recordedAt", CultureInfo.InvariantCulture)
        Scope = scope vector
        PayloadCommitment = Convert.FromHexString(text vector "payloadCommitment")
    }

let private withVectors action =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(
                Path.Combine(RepositoryRoot.find (), "tests/fixtures/ledger-entry-vectors.json")
            )
        )

    action (document.RootElement.GetProperty("vectors").EnumerateArray() |> Seq.toList)

let private vectors () =
    withVectors (fun vectors ->
        Expect.equal vectors.Length 3 "All independent vectors"

        for vector in vectors do
            let payload =
                {
                    Request = Encoding.UTF8.GetBytes(text vector "requestUtf8")
                    Snapshot = Encoding.UTF8.GetBytes(text vector "snapshotUtf8")
                }

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
                "All 185 fixed bytes"

            Expect.equal
                (Convert.ToHexStringLower(LedgerEntry.hash entry))
                (text vector "entryHash")
                "Independent hash")

let private changedHash (value: byte array) =
    let copy = Array.copy value
    copy[0] <- copy[0] ^^^ 1uy
    copy

let private allMetadataFields () =
    withVectors (fun vectors ->
        let entry = metadata vectors.Head

        let case =
            match entry.Scope with
            | LedgerScope.Case value -> value
            | _ -> failtest "Case seed required"

        let otherId = Guid.Parse "40000000-0000-4000-8000-000000000004"

        let changes =
            [
                { entry with InstallationId = otherId }
                { entry with Sequence = 2UL }
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
                    Scope = LedgerScope.Authority
                }
            ]

        let scopes =
            [
                { case with CaseId = otherId }
                { case with Revision = 2L }
                { case with
                    Act = LedgerAct.Command CommandKind.Close
                }
                { case with
                    BusinessDate = case.BusinessDate.AddDays 1
                }
                { case with RuleRevision = 3us }
            ]

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
        let! sequence = Gen.uint64 (Range.constant 1UL UInt64.MaxValue)
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
            testCase "all three independent ledger vectors reproduce every byte" vectors
            testCase "each fixed metadata field affects the hash" allMetadataFields
            testCase "act codes have an independent append-only expectation" (fun () ->
                let commands =
                    [
                        CommandKind.Open
                        CommandKind.AmendRegistration
                        CommandKind.CorrectCase
                        CommandKind.Decide
                        CommandKind.WithdrawDecision
                        CommandKind.RecordPayment
                        CommandKind.ClearPayment
                        CommandKind.Close
                        CommandKind.Reopen
                    ]

                let acts =
                    List.map LedgerAct.Command commands
                    @ List.map
                        LedgerAct.Stewardship
                        [ StewardshipKind.Void; StewardshipKind.Reinstate; StewardshipKind.Erase ]

                Expect.equal
                    (List.map LedgerEntry.actCode acts)
                    [ 1uy .. 12uy ]
                    "Durable act codes")
            testCase "payload framing round trips arbitrary bytes" (fun () ->
                PropertyHarness.run "CC-PROP-LEDGER-PAYLOAD-001" framingProperty)
            testCase "commitment changes affect hashes across the sequence range" (fun () ->
                PropertyHarness.run "CC-PROP-LEDGER-HASH-001" hashProperty)
            testCase
                "erasure leaves recomputation independent of payload nonce and request digest"
                (fun () ->
                    withVectors (fun vectors ->
                        for vector in vectors do
                            let entry = metadata vector
                            let hash = LedgerEntry.hash entry
                            let payload = text vector "payload" |> Convert.FromHexString
                            let nonce = text vector "payloadNonce" |> Convert.FromHexString

                            let digest =
                                text vector "requestUtf8"
                                |> Encoding.UTF8.GetBytes
                                |> LedgerEntry.requestDigest

                            Array.Clear payload
                            Array.Clear nonce
                            Array.Clear digest

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
                                { entry with Sequence = 0UL }
                                { entry with
                                    RecordedAt = entry.RecordedAt.AddTicks 1L
                                }
                            ]

                        for invalid in cases do
                            Expect.throwsT<ArgumentException>
                                (fun () -> LedgerEntry.hash invalid |> ignore)
                                "Invalid metadata refused"

                        Expect.throwsT<ArgumentException>
                            (fun () -> LedgerEntry.commitment [||] [||] |> ignore)
                            "Nonce width refused"))
            testCase
                "case metadata guards reject invalid identifiers revisions and rule revisions"
                (fun () ->
                    withVectors (fun vectors ->
                        let entry = metadata vectors.Head

                        let case =
                            match entry.Scope with
                            | LedgerScope.Case value -> value
                            | _ -> failtest "Case seed required"

                        for invalid in
                            [
                                { case with CaseId = Guid.Empty }
                                { case with Revision = 0L }
                                { case with Revision = Int64.MaxValue }
                                { case with RuleRevision = 0us }
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
                        (Convert.ToHexStringLower bytes[114..121])
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

                        let case =
                            match entry.Scope with
                            | LedgerScope.Case value -> value
                            | _ -> failtest "Case seed required"

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
                                    Scope = LedgerScope.Authority
                                }

                        Expect.equal caseBytes[81] 1uy "Case kind"
                        Expect.equal authorityBytes[81] 2uy "Authority kind"
                        Expect.equal caseBytes[147..150] [| 0uy; 0uy; 0uy; 0uy |] "Real day zero"))
        ]
