module ClaimCore.Tests.HumanReadBoundaryTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private required value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private services =
    ClaimCore.Tests.DecisionFixtures.services
        ClaimCore.Tests.DecisionFixtures.installation
        ClaimCore.Tests.DecisionFixtures.calendar
        ClaimCore.Tests.DecisionFixtures.owner

[<Tests>]
let tests =
    testList
        "human read transport boundary"
        [
            testCaseAsync
                "a selected representation projects one use-case result without a second invocation"
                (async {
                    let mutable invoked = 0

                    let route =
                        ClaimCore.Server.Route.representedRead
                            Api.caseGet
                            HumanCaseDocument.ofCore
                            (fun _ _ _ ->
                                invoked <- invoked + 1
                                Task.FromResult(Outcome.Succeeded ResponseExamples.caseRead))

                    let input =
                        Api.caseGet.Input.Write
                            {
                                Target = CaseSelector.Reference "CASE-1"
                            }

                    let prepared = route.Prepare input |> required

                    let! human =
                        prepared.Invoke
                            Representation.Human
                            (Some services)
                            CancellationToken.None
                            ignore
                        |> Async.AwaitTask

                    Expect.equal invoked 1 "Exactly one use-case invocation for HUMAN"

                    Expect.equal
                        (JsonValue.tryMember "representation" human.Wire)
                        (Some(JsonValue.String "HUMAN"))
                        "Explicit representation"

                    let outcome = JsonValue.tryMember "outcome" human.Wire |> Option.get

                    Expect.isNone
                        (JsonValue.tryMember "data" outcome)
                        "HUMAN carries no machine result"

                    let document = JsonValue.tryMember "document" outcome |> Option.get

                    Expect.isSome
                        (JsonValue.tryMember "screen" document)
                        "The core document is supplied"

                    Expect.isSome
                        (JsonValue.tryMember "subject" document)
                        "Document identity is separate from display"

                    let! data =
                        prepared.Invoke
                            Representation.Data
                            (Some services)
                            CancellationToken.None
                            ignore
                        |> Async.AwaitTask

                    Expect.equal
                        invoked
                        2
                        "Exactly one additional invocation for a separate DATA observation"

                    Expect.equal
                        (JsonValue.tryMember "representation" data.Wire)
                        (Some(JsonValue.String "DATA"))
                        "Explicit DATA"

                    let outcome = JsonValue.tryMember "outcome" data.Wire |> Option.get

                    Expect.isSome
                        (JsonValue.tryMember "data" outcome)
                        "Typed data remains available"

                    Expect.isNone (JsonValue.tryMember "view" outcome) "DATA has no Screen sibling"

                    Expect.isNone
                        (JsonValue.tryMember "document" outcome)
                        "DATA has no human document"

                    Expect.equal
                        human.Class
                        data.Class
                        "Representation does not alter use-case outcome"
                })
            testCase
                "erasure masks input seeds even if a contradictory producer supplies editable flags"
                (fun () ->
                    let current = ResponseExamples.caseRead
                    let secret = HandlingSummary.parse "Seed must be removed" |> required

                    let erased =
                        { current with
                            Snapshot =
                                { current.Snapshot with
                                    State = CaseState.Erased
                                }
                            Work =
                                { current.Work with
                                    Summary = Some secret
                                    CanUpdateSummary = true
                                    CanAssign = true
                                    CanCreateReminder = true
                                }
                            Notes = { current.Notes with CanAdd = true }
                        }

                    let document = HumanCaseDocument.ofCore erased

                    Expect.isEmpty
                        document.Actions
                        "Erased resources have no stale controls or private seeds"

                    Expect.equal
                        document.Disclosure
                        HumanDisclosure.Erased
                        "Erasure remains explicit"

                    let payload =
                        RepresentationReply.Human
                            {
                                HumanReadReply.Outcome = HumanReadOutcome.Succeeded document
                            }

                    let raw = Codec.encode Api.caseGet.Output payload

                    Expect.isOk
                        (Codec.validate Limits.MaximumResponseBytes Api.caseGet.Output raw)
                        "A properly masked human response is structurally valid"

                    Expect.isFalse
                        (Encoding.UTF8.GetString(raw).Contains "Seed must be removed")
                        "No private seed or display survives erasure")
            testCase
                "the actual native human renderer ignores contradictory editable input seeds"
                (fun () ->
                    let original = HumanCaseDocument.ofCore ResponseExamples.caseRead

                    let bait =
                        RegistrationInput.replace
                            [ BusinessField.ClaimantName, "Input bait must not display" ]
                            Inputs.registration
                        |> required

                    let action =
                        TaskAffordances.caseAction
                            CaseActKind.AmendRegistration
                            ResponseExamples.snapshot.CaseId
                            ResponseExamples.snapshot.Revision
                            (RegistrationInput.values bait |> TaskAffordances.fields)
                            (TaskAffordances.authored (
                                TaskId.Operation(ActKind.Case CaseActKind.AmendRegistration)
                            ))
                            Map.empty

                    let screen =
                        Screen.create
                            [
                                ScreenEntries.entryRegion
                                    RegionRole.Facts
                                    None
                                    [
                                        ScreenEntries.value
                                            "opaque"
                                            "label.details"
                                            Prominence.Primary
                                            (PresentedValue.Text "Core caption Ω")
                                    ]
                            ]

                    let reply =
                        {
                            HumanReadReply.Outcome =
                                HumanReadOutcome.Succeeded
                                    { original with
                                        Screen = screen
                                        Actions = [ action ]
                                    }
                        }

                    let typed = CliReplies.ofValue Api.caseGet (RepresentationReply.Human reply)

                    let frame =
                        {
                            FrameReply.Endpoint = Some Api.caseGet.Id
                            OperationId = None
                            Delivery = DeliveryState.Delivered
                            Result = FrameResult.Reply typed
                        }

                    let text = ReplyText.bytes "en" frame |> Encoding.UTF8.GetString

                    Expect.stringContains
                        text
                        "Core caption Ω"
                        "Actual CLI consumes the supplied document"

                    Expect.isFalse
                        (text.Contains "Input bait must not display")
                        "Editable input cannot override a saved fact")
            testCase
                "authoring seeds cannot survive erased disclosure or acquire a foreign binding"
                (fun () ->
                    let document = HumanCaseDocument.ofCore ResponseExamples.caseRead

                    let action =
                        TaskAffordances.caseAction
                            CaseActKind.AmendRegistration
                            ResponseExamples.snapshot.CaseId
                            ResponseExamples.snapshot.Revision
                            (RegistrationInput.values Inputs.registration |> TaskAffordances.fields)
                            []
                            Map.empty

                    Expect.throws
                        (fun () ->
                            HumanDocument.create
                                document.Subject
                                document.Screen
                                [ action ]
                                []
                                None
                                document.Basis
                                HumanDisclosure.Erased
                                document.History
                            |> ignore)
                        "The producer rejects erased seed injection"

                    Expect.throws
                        (fun () ->
                            Affordance.create
                                action.Task
                                (OperationBinding.People None)
                                action.Seeds
                                []
                                Map.empty
                                false
                                None
                            |> ignore)
                        "An operation cannot acquire another subject's binding")
            testCase
                "native selection is correlated without entering frozen business bytes"
                (fun () ->
                    let input: OperationInput =
                        OperationInput.Case
                            {
                                OperationId =
                                    OperationId.value ResponseExamples.receipt.OperationId
                                Target = CaseSelector.Reference "CASE-1"
                                ExpectedRevision = 1L
                                Act = CaseAct.Bare BareAct.Close
                            }

                    let original =
                        (Frames.create Api.operationExecute
                         << ClaimCore.TestSupport.Submissions.ofCommand None)
                            input
                        |> required

                    let human = Frames.withRepresentation Representation.Human original
                    let data = Frames.withRepresentation Representation.Data original

                    Expect.equal
                        (Frames.inputBytes human)
                        (Frames.inputBytes data)
                        "Identical canonical business bytes"

                    Expect.equal
                        (Frames.operationId human)
                        (Frames.operationId data)
                        "Identical executable identity"

                    Expect.equal
                        (Frames.encodeInvocation human)
                        (Frames.encodeInvocation data)
                        "Machine retry frames do not persist an output selection"

                    let read =
                        Frames.create
                            Api.caseGet
                            {
                                Target = CaseSelector.Reference "CASE-1"
                            }
                        |> required
                        |> Frames.withRepresentation Representation.Human

                    let response =
                        Api.caseGet.Output.Write(
                            RepresentationReply.Data(
                                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                            )
                        )

                    Expect.isError
                        (CliReply.result read response)
                        "A valid DATA body cannot settle a HUMAN request"

                    let matching =
                        Api.caseGet.Output.Write(
                            HumanReplies.read
                                HumanCaseDocument.ofCore
                                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                            |> RepresentationReply.Human
                        )

                    Expect.isOk
                        (CliReply.result read matching)
                        "The matching HUMAN reply is actually admitted")
            testCase
                "every ordinary actor read has distinct HUMAN and DATA transport branches"
                (fun () ->
                    for query in QueryTaskId.all |> List.filter ((<>) QueryTaskId.OperationGet) do
                        let route = Api.forActor (ActorEndpoint.Query query)
                        let samples = ResponseExamples.samples route.Token
                        let mutable representations = Set.empty

                        for bytes in samples do
                            let value =
                                StrictJson.read Limits.MaximumResponseBytes bytes |> required

                            Expect.isOk
                                (route.ValidateOutput value)
                                "Actual represented response admits structurally"

                            let representation = JsonValue.tryMember "representation" value
                            representations <- Set.add representation representations
                            let outcome = JsonValue.tryMember "outcome" value |> Option.get

                            match representation, JsonValue.tryMember "kind" outcome with
                            | Some(JsonValue.String "HUMAN"), Some(JsonValue.String "SUCCEEDED") ->
                                Expect.isNone
                                    (JsonValue.tryMember "data" outcome)
                                    "No machine success projection in HUMAN"

                                Expect.isSome
                                    (JsonValue.tryMember "document" outcome)
                                    "Human resource document"
                            | Some(JsonValue.String "DATA"), _ ->
                                let rec containsScreen =
                                    function
                                    | JsonValue.Object fields ->
                                        fields
                                        |> List.exists (fun (key, value) ->
                                            ((key = "screen" || key = "view")
                                             && (match value with
                                                 | JsonValue.Object fields ->
                                                     fields
                                                     |> List.exists (fun (name, _) ->
                                                         name = "regions")
                                                 | _ -> false))
                                            || containsScreen value)
                                    | JsonValue.Array values ->
                                        values |> List.exists containsScreen
                                    | _ -> false

                                Expect.isFalse
                                    (containsScreen value)
                                    (route.Token
                                     + ": no Screen in DATA, including nested diagnostics")
                            | _ -> ()

                        Expect.equal
                            representations
                            (Set.ofList
                                [ Some(JsonValue.String "HUMAN"); Some(JsonValue.String "DATA") ])
                            "Both branches exercise their actual endpoint codec")
        ]
