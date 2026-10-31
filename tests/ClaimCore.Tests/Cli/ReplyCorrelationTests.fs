module ClaimCore.Tests.ReplyCorrelationTests

open System
open System.Net.Http
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport
open ClaimCore.Tests.CliTransportFixture

let private receipt operation reference =
    { CliReplyFixtures.receipt with
        OperationId = operation
        Subject =
            ReceiptSubject.Case
                {
                    CaseId = CaseId.ofOperationId operation
                    Reference = CaseFixtures.reference reference
                    Revision = Revision.first
                    Act = CaseActKind.Open
                    Snapshot = SnapshotDisclosure.Withheld
                }
    }

let private body invocation =
    let operation = Frames.operationId invocation |> Option.get

    let reference =
        match
            JsonValue.tryMember "target" (Frames.input invocation)
            |> Option.bind (JsonValue.tryMember "caseReference")
        with
        | Some(JsonValue.String reference) -> reference
        | _ -> "UNIT-001"

    let accepted = receipt (CaseFixtures.operationId operation) reference

    match (Frames.endpoint invocation).Token with
    | "operation.execute" ->
        Api.operationExecute.Output.Write
            {
                ExecuteReply.OperationId = operation
                Outcome = ExecuteResult.Accepted accepted
            }
    | "operation.preview" ->
        Api.operationPreview.Output.Write
            {
                PreviewReply.OperationId = operation
                Outcome = Outcome.Succeeded(Previewed.AlreadyAccepted accepted)
            }
    | "operation.observe" ->
        Api.operationObserve.Output.Write
            {
                OperationReply.OperationId = operation
                Outcome = Outcome.Succeeded accepted
            }
    | _ ->
        Api.operationResolve.Output.Write
            {
                OperationReply.OperationId = operation
                Outcome = Outcome.Succeeded Resolved.NotAccepted
            }

let private exchange invocation value =
    use handler =
        new Handler(fun _ -> Task.FromResult(response 200 (WireWriter.bytes value)))

    send handler invocation

[<Tests>]
let tests =
    testList
        "client reply correlation"
        [
            testCase
                "real HTTP admission requires matching operation envelopes for every operation endpoint"
                (fun () ->
                    for endpoint in
                        [
                            "operation.execute"
                            "operation.preview"
                            "operation.observe"
                            "operation.resolve"
                        ] do
                        let invocation = frame endpoint
                        let value = body invocation
                        let matching = exchange invocation value

                        Expect.equal
                            matching.ExitCode
                            (if endpoint = "operation.resolve" then 2 else 0)
                            ("Matching transport control: " + endpoint)

                        let mismatched =
                            match value with
                            | JsonValue.Object members ->
                                JsonValue.Object(
                                    members
                                    |> List.map (fun (name, value) ->
                                        name,
                                        if name = "operationId" then
                                            JsonValue.String
                                                "90000000-0000-4000-8000-000000000099"
                                        else
                                            value)
                                )
                            | _ -> failtest "Envelope must be an object"

                        Expect.isOk
                            ((Frames.endpoint invocation).NormalizeOutput mismatched)
                            "The negative control remains schema-valid"

                        let refused = exchange invocation mismatched

                        Expect.equal
                            refused.Frame.Delivery
                            DeliveryState.Uncertain
                            "A post-dispatch mismatched reply cannot settle the operation"

                        Expect.isTrue (refused.ExitCode <> 0) "It never becomes acceptance")
            testCase "reference correlation folds ASCII only" (fun () ->
                let operation = Guid.Parse "10000000-0000-4000-8000-000000000001"

                let invoke reference =
                    Frames.create
                        Api.operationExecute
                        (OperationInput.Case
                            {
                                OperationId = operation
                                Target = CaseSelector.Reference reference
                                ExpectedRevision = 1L
                                Act = AuthoredCaseAct.Bare BareAct.Close
                            })
                    |> Result.defaultWith (fun _ ->
                        failtest "Raw reference request must structurally admit")

                for authored, retained in [ "ß", "SS"; "ſ", "S" ] do
                    let accepted =
                        { CliReplyFixtures.receipt with
                            OperationId = CaseFixtures.operationId operation
                            Subject =
                                ReceiptSubject.Case
                                    {
                                        CaseId =
                                            CaseFixtures.caseId (
                                                Guid.Parse "20000000-0000-4000-8000-000000000001"
                                            )
                                        Reference = CaseFixtures.reference retained
                                        Revision = CaseFixtures.revision 2L
                                        Act = CaseActKind.Close
                                        Snapshot = SnapshotDisclosure.Withheld
                                    }
                        }

                    let value =
                        Api.operationExecute.Output.Write
                            {
                                OperationId = operation
                                Outcome = ExecuteResult.Accepted accepted
                            }

                    Expect.equal
                        (exchange (invoke (retained.ToLowerInvariant())) value).ExitCode
                        0
                        "ASCII case variants identify the same retained reference"

                    let refused = exchange (invoke authored) value

                    Expect.equal
                        refused.ExitCode
                        4
                        "Unicode folding cannot redirect a dispatched mutation"

                    Expect.equal
                        refused.Frame.Delivery
                        DeliveryState.Uncertain
                        "A foreign reference cannot settle acceptance")
            testCase
                "a matching execute envelope cannot hide a different receipt operation"
                (fun () ->
                    let invocation = frame "operation.execute"
                    let operation = Frames.operationId invocation |> Option.get

                    let wrong =
                        receipt
                            (CaseFixtures.operationId (
                                Guid.Parse "90000000-0000-4000-8000-000000000099"
                            ))
                            "UNIT-001"

                    let value =
                        Api.operationExecute.Output.Write
                            {
                                ExecuteReply.OperationId = operation
                                Outcome = ExecuteResult.Accepted wrong
                            }

                    let refused = exchange invocation value

                    Expect.equal
                        refused.ExitCode
                        4
                        "The schema-valid mismatched receipt remains uncertain"

                    Expect.equal
                        refused.Frame.Delivery
                        DeliveryState.Uncertain
                        "No definitive business outcome leaks through")
        ]
