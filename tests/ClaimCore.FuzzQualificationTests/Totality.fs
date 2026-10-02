module ClaimCore.FuzzQualificationTests.Totality

open System
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Contracts
open ClaimCore.Cli
open ClaimCore.RecordFormat
open ClaimCore.Tests.PropertyHarness

/// A trust boundary must be total: every input either decodes or is refused with a typed value.
/// An escaping exception is not a safe refusal - it degrades a diagnosable rejection into a CLI
/// "unexpected internal failure" or an HTTP 500, and loses the reason the input was bad.
let private total (decode: 'input -> 'result) (input: 'input) =
    try
        decode input |> ignore
        true
    with _ ->
        false

let private property (generator: Gen<'input>) (decode: 'input -> unit) =
    property {
        let! input = generator
        return total decode input
    }

let private strictJson (bytes: byte array) =
    match StrictJson.parseDocument 131072 bytes with
    | Ok document -> document.Dispose()
    | Error _ -> ()

let private invocation (bytes: byte array) =
    match StrictJson.parseDocument 131072 bytes with
    | Error _ -> ()
    | Ok document ->
        use source = document
        CliRemoteInvocation.decode source.RootElement |> ignore

/// A valid canonical encoding, mutated one byte at a time, reaches decoder interiors that uniform
/// noise almost never does.
let private validRecord =
    lazy
        (ClaimCore.Tests.Fixtures.request 0L (Command.Open ClaimCore.Tests.Fixtures.registration)
         |> RequestRecord.encode)

let private snapshot = lazy (ClaimCore.Tests.Fixtures.opened () |> Claim.view)
let private validSnapshot = lazy (CaseRecord.encodeSnapshot snapshot.Value)

let private recordCorpus =
    Gen.choice
        [
            Corpora.jsonLike
            Corpora.mutated validRecord.Value
            Corpora.mutated validSnapshot.Value
        ]

let private envelope = lazy (ClaimCore.Tests.RecoveryArtifactFixture.artifact ())

let private validEnvelope =
    lazy (ClaimCore.Tests.RecoveryArtifactFixture.encode envelope.Value)

let private envelopeCorpus =
    Gen.choice [ Corpora.jsonLike; Corpora.mutated validEnvelope.Value ]

let private decodeEnvelope bytes =
    let fixture = envelope.Value

    ClaimCore.Tests.RecoveryArtifactFixture.decode
        ClaimCore.Tests.RecoveryArtifactFixture.now
        fixture.Epoch
        bytes

let private caseListCursorBoundary =
    let principal =
        PrincipalKey.human "https://issuer.example.test/realms/fuzz" "synthetic-reader"
        |> Result.defaultWith (fun _ -> invalidOp "Synthetic principal must be valid.")

    let binding =
        {
            Principal = principal
            ActorId = Guid.Parse("40000000-0000-4000-8000-000000000001")
            GrantRevision = 1L
        }

    fun (payload: byte array) ->
        let protection =
            { new ICaseListCursorProtection with
                member _.Seal(_) =
                    invalidOp "Decode-only property boundary."

                member _.Open(_) = Some(Array.copy payload)
            }

        CaseListCursorCodec.decode
            protection
            binding
            1L
            1
            (DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero))
            "synthetic-token"
        |> ignore

let private httpCodecs bytes =
    HttpInput.logout bytes |> ignore
    HttpInput.draft bytes |> ignore
    HttpInput.caseReference bytes |> ignore
    HttpInput.page 50 bytes |> ignore
    HttpInput.history 50 bytes |> ignore
    HttpInput.recoveryPage 50 bytes |> ignore
    HttpInput.recoveryInspect 50 bytes |> ignore
    HttpInput.operationId bytes |> ignore
    HttpInput.resolve bytes |> ignore
    HttpInput.dismiss bytes |> ignore
    HttpManagementInput.register bytes |> ignore
    HttpManagementInput.setGrant bytes |> ignore
    HttpManagementInput.setEnabled bytes |> ignore
    HttpManagementInput.observe bytes |> ignore
    HttpSignerApprovalInput.approve bytes |> ignore
    HttpCopyDeletionApprovalInput.approve bytes |> ignore
    HttpCopyAdoptionApprovalInput.approve bytes |> ignore
    HttpWriterHandoffApprovalInput.approve bytes |> ignore
    HttpRealDataActivationInput.review bytes |> ignore
    HttpRealDataActivationInput.approve bytes |> ignore
    HttpLifecycleInput.review bytes |> ignore
    HttpLifecycleInput.apply bytes |> ignore
    HttpLifecycleInput.approve bytes |> ignore
    HttpTombstoneInput.review bytes |> ignore
    HttpTombstoneInput.approve bytes |> ignore
    HttpTombstoneInput.hold bytes |> ignore
    HttpTombstoneTerminalInput.approve bytes |> ignore

let private recordTests =
    [
        testCase "canonical record decoding refuses hostile input without throwing" (fun () ->
            run
                "CC-FUZZ-RECORD-001"
                (property recordCorpus (fun bytes ->
                    RequestRecord.decode 131072 bytes |> ignore
                    CaseRecord.decodeSnapshot bytes |> ignore)))

        testCase "recovery envelope decoding refuses hostile input without throwing" (fun () ->
            run
                "CC-FUZZ-ENVELOPE-001"
                (property envelopeCorpus (fun bytes -> decodeEnvelope bytes |> ignore)))

        testCase
            "decoder seeds prove command snapshot and authenticated recovery acceptance"
            (fun () ->
                Expect.isOk
                    (RequestRecord.decode 131072 validRecord.Value)
                    "Command seed reaches decoding"

                Expect.isTrue
                    (CaseRecord.decodeSnapshot validSnapshot.Value = Ok snapshot.Value)
                    "Snapshot seed meaning"

                let restored =
                    decodeEnvelope validEnvelope.Value |> ClaimCore.Tests.Fixtures.accepted

                Expect.isTrue
                    (restored.CanonicalRequest = envelope.Value.CanonicalRequest
                     && restored.OperationId = envelope.Value.OperationId
                     && restored.CaseId = envelope.Value.CaseId)
                    "Authenticated seed retains exact operation bytes and identities")

    ]

let tests =
    [
        testCase
            "[CC-ARCH-001] contract-owned HTTP codecs refuse hostile bytes without throwing"
            (fun () ->
                run "CC-FUZZ-HTTP-001" (property Corpora.jsonLike httpCodecs)
                run "CC-FUZZ-HTTP-BYTES-001" (property Corpora.arbitraryBytes httpCodecs))
        testCase "strict JSON parsing refuses hostile input without throwing" (fun () ->
            run "CC-FUZZ-JSON-001" (property Corpora.jsonLike strictJson))

        testCase "CLI invocation framing refuses hostile input without throwing" (fun () ->
            run "CC-FUZZ-INVOCATION-001" (property Corpora.jsonLike invocation))


        testCase "opaque cursors refuse hostile tokens without throwing" (fun () ->
            run
                "CC-FUZZ-CURSOR-001"
                (property Corpora.cursorText (fun token ->
                    HistoryCursor.decode token |> ignore
                    RecoveryCursorCodec.decode token |> ignore
                    RecoveryAttemptCursorCodec.decode token |> ignore
                    caseListCursorBoundary (Encoding.UTF8.GetBytes token)))

            run
                "CC-FUZZ-CASE-LIST-CURSOR-001"
                (property Corpora.arbitraryBytes caseListCursorBoundary))
    ]
    @ recordTests
    |> testList "boundary decoding totality"
