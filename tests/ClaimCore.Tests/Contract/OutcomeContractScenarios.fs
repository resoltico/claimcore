module ClaimCore.Tests.OutcomeContractScenarios

open ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.ContractScenarioInputs


[<NoEquality; NoComparison>]
type internal ActionExpectation =
    {
        Refusal: Refusal
        Action: RecommendedAction
        Token: string
        Field: string option
        Revision: int64 option
    }

let internal contextCodec () =
    admitted
        Api.context.Output
        (Outcome.Succeeded
            {
                Runtime =
                    {
                        ProductVersion = "0.10.0"
                        EffectiveBusinessDate = today
                        TimeZoneId = "Etc/UTC"
                    }
                ActorId =
                    ActorId.create (System.Guid.Parse "10000000-0000-4000-8000-000000000001")
                    |> Option.get
                HasAssignedCases = false
                Capability = []
                View = []
                Offers = []
            })

    |> ignore

let internal queryCodecs () =
    let snapshot = Case.snapshot (opened ())

    for view in
        [
            snapshot
            { snapshot with
                State = ClaimCore.Domain.CaseState.Erased
            }
        ] do
        admitted
            Api.caseGet.Output
            (Outcome.Succeeded
                {
                    AssignedToYou = false
                    Snapshot = view
                    Advice = (CaseAdvice.create [] None [] |> Result.defaultWith invalidOp)
                })

        |> ignore

    admitted
        Api.caseList.Output
        (Outcome.Succeeded
            {
                Items = []
                NextCursor = None
                Offers = []
            })

    |> ignore

    admitted Api.caseHistory.Output (Outcome.Succeeded { Entries = []; NextCursor = None })

    |> ignore

    admitted Api.operationObserve.Output (Outcome.Succeeded authority)

    |> ignore

    admitted Api.authorityList.Output (Outcome.Succeeded { Items = []; NextCursor = None })

    |> ignore


let internal executeOutcomes () =
    for receiptValue in [ receipt; authority ] do
        for outcome, exit in
            [
                ExecuteResult.Accepted receiptValue, 0
                ExecuteResult.AlreadyAccepted receiptValue, 0
                ExecuteResult.Rejected(Refusal.Access AccessRefusal.ActNotPermitted), 2
                ExecuteResult.Failed CoreFault.StoreUnavailable, 3
                ExecuteResult.OutcomeUnknown, 4
            ] do
            let reply: ExecuteReply =
                {

                    OperationId = id
                    Outcome = outcome
                }

            admitted Api.operationExecute.Output reply |> ignore

            let frame: ClaimCore.Cli.FrameReply =
                {
                    Endpoint =
                        Some(EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute))
                    OperationId = Some id
                    Delivery =
                        if exit = 4 then
                            DeliveryState.Uncertain
                        else
                            DeliveryState.Delivered
                    Result =
                        FrameResult.Reply(
                            ClaimCore.Tests.CliReplies.ofValue Api.operationExecute reply
                        )
                }

            Expect.equal
                (ClaimCore.Cli.Frames.decode (ClaimCore.Cli.Frames.encode frame)
                 |> Result.map CliReplies.frameFacts)
                (Ok(CliReplies.frameFacts frame))
                "CLI frames share mutation outcomes"

            Expect.equal
                (ClaimCore.Cli.Frames.exitCode frame)
                exit
                "Mutation scope preserves exit semantics"

let internal disclosures () =
    for snapshot, tag in
        [ SnapshotDisclosure.Withheld, "WITHHELD"; SnapshotDisclosure.Erased, "ERASED" ] do
        let value =
            { receipt with
                Subject =
                    ClaimCore.Domain.ReceiptSubject.Case
                        {
                            Reference = CaseFixtures.reference "UNIT-001"
                            Revision = CaseFixtures.revision 1L
                            Act = CaseActKind.Open
                            Snapshot = snapshot
                        }
            }

        let result = admitted Api.operationObserve.Output (Outcome.Succeeded value)

        Expect.equal
            (result
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("subject")
                .GetProperty("snapshot")
                .GetProperty("kind")
                .GetString())
            tag
            "Disclosure remains explicit"

let internal previewCodecs () =
    let review: AdvisoryReview =
        {
            Before = None
            Proposed = Case.snapshot (opened ())
            Changes = []
            Context =
                {
                    ProductVersion = "0.10.0"
                    EffectiveBusinessDate = today
                    TimeZoneId = "Etc/UTC"
                }
        }

    for value in
        [
            (Previewed.Reviewed >> Outcome.Succeeded) (OperationReview.Case review)
            (Previewed.AlreadyAccepted >> Outcome.Succeeded) receipt
            Outcome.Rejected(ClaimCore.Domain.Refusal.Access AccessRefusal.ActNotPermitted)
            Outcome.Failed CoreFault.StoreIntegrityError
        ] do
        admitted Api.operationPreview.Output { OperationId = id; Outcome = value }
        |> ignore

let internal diagnostics () =
    for fault, _ in CoreFault.all do
        admitted Api.caseGet.Output (Outcome.Failed fault) |> ignore

    for value, expectedCode in
        [
            ClaimCore.Domain.Refusal.InvalidCursor, "INVALID_CURSOR"
            (ClaimCore.Domain.Refusal.Authority AuthorityRefusal.LastOwner), "LAST_OWNER"
            (ClaimCore.Domain.Refusal.Access AccessRefusal.ActNotPermitted), "ACT_NOT_PERMITTED"
            (ClaimCore.Domain.Refusal.Access AccessRefusal.ResourceUnavailable),
            "RESOURCE_UNAVAILABLE"
        ] do
        let result = admitted Api.caseGet.Output (Outcome.Rejected value)

        Expect.equal
            (result.GetProperty("outcome").GetProperty("data").GetProperty("code").GetString())
            expectedCode
            "The refusal carries its stable code"

let internal actionExpectations =
    [
        {
            Refusal = (Refusal.invalidInput (InputTarget.Reason, Violation.TextRequired))
            Action = RecommendedAction.CorrectInput
            Token = "CORRECT_INPUT"
            Field = Some "reason"
            Revision = None
        }
        {
            Refusal = Refusal.invalidInput (InputTarget.Limit, Violation.PageLimitRange 50)
            Action = RecommendedAction.CorrectInput
            Token = "CORRECT_INPUT"
            Field = Some "limit"
            Revision = None
        }
        {
            Refusal = ClaimCore.Domain.Refusal.InvalidCursor
            Action = RecommendedAction.ReadCurrent
            Token = "READ_CURRENT"
            Field = Some "cursor"
            Revision = None
        }
        {
            Refusal = (Refusal.RevisionConflict(CaseFixtures.revision 7L))
            Action = RecommendedAction.ReadCurrent
            Token = "READ_CURRENT"
            Field = None
            Revision = Some 7L
        }

    ]

let internal actionEnums () =
    for expectation in actionExpectations do
        let refusal = expectation.Refusal
        let expected = expectation.Action

        Expect.equal
            refusal.Action
            expected
            "Input correction and current-state recovery are distinct"

let internal diagnosticRevisions () =
    let reason = Refusal.RevisionConflict(CaseFixtures.revision 0L)
    let codec = DiagnosticCodecs.diagnostic

    Expect.equal
        (Codec.encode codec reason |> Codec.decode 10000 codec)
        (Ok reason)
        "An absent record can report actual revision zero"

    Expect.throws
        (fun () ->
            Codec.encode codec (Refusal.RevisionConflict(CaseFixtures.revision (-1L)))
            |> ignore)
        "Negative actual revisions remain invalid"

    let snapshot =
        { Case.snapshot (opened ()) with
            Revision = CaseFixtures.revision 0L
        }

    Expect.throws
        (fun () -> Codec.encode ResponseCodecs.caseSnapshot snapshot |> ignore)
        "An accepted snapshot revision stays positive"

    let invalidReceipt =
        match receipt.Subject with
        | ReceiptSubject.Case target ->
            { receipt with
                Subject =
                    ReceiptSubject.Case
                        { target with
                            Revision = CaseFixtures.revision 0L
                        }
            }
        | _ -> failtest "Case receipt fixture"

    Expect.throws
        (fun () -> Codec.encode ReceiptCodecs.receipt invalidReceipt |> ignore)
        "An accepted receipt revision stays positive"

let internal actionTokens () =
    diagnosticRevisions ()

    for expectation in actionExpectations do
        let refusal = expectation.Refusal
        let expected = expectation.Token
        let field = expectation.Field
        let revision = expectation.Revision
        let result = admitted Api.caseGet.Output (Outcome.Rejected refusal)

        let data = result.GetProperty("outcome").GetProperty("data")

        Expect.equal (data.GetProperty("action").GetString()) expected "Literal wire guidance"

        match refusal with
        | Refusal.InvalidInput issues ->
            Expect.isFalse
                (data.TryGetProperty("target") |> fst)
                "An aggregate never guesses a top-level target"

            let encodedIssues = data.GetProperty("issues").EnumerateArray() |> Seq.toList

            Expect.equal
                encodedIssues.Length
                (InputIssues.toList issues |> List.length)
                "Every issue survives"

            field
            |> Option.iter (fun target ->
                Expect.equal
                    (encodedIssues.Head.GetProperty("field").GetString())
                    target
                    "The issue retains its own field")
        | _ ->
            let mutable encodedTarget = Unchecked.defaultof<JsonElement>
            let hasTarget = data.TryGetProperty("target", &encodedTarget)

            Expect.equal
                hasTarget
                field.IsSome
                "Only applicable non-input target metadata is serialized"

            field
            |> Option.iter (fun target ->
                Expect.equal (encodedTarget.GetString()) target "Literal target")

        let mutable encodedRevision = Unchecked.defaultof<JsonElement>
        let hasRevision = data.TryGetProperty("actualRevision", &encodedRevision)
        Expect.equal hasRevision revision.IsSome "Only revision conflicts carry revision metadata"

        revision
        |> Option.iter (fun revision ->
            Expect.equal (encodedRevision.GetInt64()) revision "Literal revision")

        Expect.equal
            (refusal.ActualRevision |> Option.map Revision.value)
            revision
            "Revision metadata is retained"
