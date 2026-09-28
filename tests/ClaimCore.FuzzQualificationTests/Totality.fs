module ClaimCore.FuzzQualificationTests.Totality

open System
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.Domain
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

let private recordCorpus =
    Gen.choice [ Corpora.jsonLike; Corpora.mutated validRecord.Value ]

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

let tests =
    testList
        "boundary decoding totality"
        [
            testCase "strict JSON parsing refuses hostile input without throwing" (fun () ->
                run "CC-FUZZ-JSON-001" (property Corpora.jsonLike strictJson))

            testCase "CLI invocation framing refuses hostile input without throwing" (fun () ->
                run "CC-FUZZ-INVOCATION-001" (property Corpora.jsonLike invocation))

            testCase "canonical record decoding refuses hostile input without throwing" (fun () ->
                run
                    "CC-FUZZ-RECORD-001"
                    (property recordCorpus (fun bytes ->
                        RequestRecord.decode 131072 bytes |> ignore
                        CaseRecord.decodeSnapshot bytes |> ignore)))

            testCase "recovery envelope decoding refuses hostile input without throwing" (fun () ->
                run
                    "CC-FUZZ-ENVELOPE-001"
                    (property recordCorpus (fun bytes ->
                        RecoveryEnvelopeV3.decode
                            131072
                            (fun _ -> None)
                            (Guid.Parse("11111111-1111-4111-8111-111111111111"))
                            1L
                            (DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero))
                            (TimeSpan.FromHours(24.0))
                            bytes
                        |> ignore)))

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
