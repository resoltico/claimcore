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
                        ClaimCore.Web.Route.representedRead
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
                        (JsonValue.tryMember "context" document)
                        "Interaction context is separate from display"

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

                    match document.Context with
                    | InteractionContext.Case context ->
                        Expect.isNone context.Seeds.Registration "No registration prefill"
                        Expect.isNone context.Seeds.Decision "No financial prefill"
                        Expect.isNone context.Seeds.Work "No work prefill"
                        Expect.isEmpty context.EditableInputs "No stale edit controls"
                        Expect.isEmpty context.Tasks "No stale write handles"
                        Expect.isNone context.Correction "No hidden correction choices"
                    | _ -> failtest "Case identity remains explicitly scoped"

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

                    let context =
                        match original.Context with
                        | InteractionContext.Case context ->
                            InteractionContext.Case(
                                { context with
                                    Tasks =
                                        TaskId.Operation(
                                            ActKind.Case CaseActKind.AmendRegistration
                                        )
                                        :: context.Tasks
                                    EditableInputs =
                                        BusinessField.ofGroup RecordGroup.Registration
                                        |> List.map (InputTarget.Field >> TaskInputId.Target)
                                    Seeds =
                                        { context.Seeds with
                                            Registration = Some bait
                                            Decision = None
                                            Work = None
                                        }
                                }
                            )
                        | _ -> failtest "Case context"

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
                                        Context = context
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
                "human seed disclosure and action references are independent structural safeguards"
                (fun () ->
                    let document = HumanCaseDocument.ofCore ResponseExamples.caseRead

                    let context =
                        match document.Context with
                        | InteractionContext.Case value -> value
                        | _ -> failtest "Case context"

                    let seed =
                        { context.Seeds with
                            Registration = Some Inputs.registration
                        }

                    let context =
                        { context with
                            Tasks =
                                TaskId.Operation(ActKind.Case CaseActKind.AmendRegistration)
                                :: context.Tasks
                            EditableInputs =
                                BusinessField.ofGroup RecordGroup.Registration
                                |> List.map (InputTarget.Field >> TaskInputId.Target)
                            Seeds = seed
                        }

                    let exposed =
                        { document with
                            Disclosure = HumanDisclosure.Erased
                            Context = InteractionContext.Case { context with Seeds = seed }
                        }

                    Expect.isError
                        (HumanDocumentCodecs.document.Validate
                            "$"
                            (HumanDocumentCodecs.document.Write
                                { document with
                                    Context = InteractionContext.Case { context with Seeds = seed }
                                }
                             |> function
                                 | JsonValue.Object fields ->
                                     JsonValue.Object(
                                         fields
                                         |> List.map (fun (name, value) ->
                                             name,
                                             if name = "disclosure" then
                                                 JsonValue.String "ERASED"
                                             else
                                                 value)
                                     )
                                 | _ -> failtest "Document object"))
                        "Erased seed injection refuses independently of the presenter"

                    Expect.throws
                        (fun () -> Codec.encode HumanDocumentCodecs.document exposed |> ignore)
                        "Producer output cannot bypass disclosure structure"

                    let actions =
                        {
                            RegionActions.Recommended = Some(ActKind.Case CaseActKind.Erase)
                            Groups = []
                        }

                    let unbound =
                        { document with
                            Screen =
                                Screen.create
                                    [
                                        ScreenEntries.region
                                            RegionRole.Recommendation
                                            None
                                            (RegionContent.Actions actions)
                                    ]
                            Context =
                                InteractionContext.Case
                                    { context with
                                        Tasks = []
                                        Correction = None
                                        EditableInputs = []
                                        Seeds =
                                            {
                                                Registration = None
                                                Decision = None
                                                Work = None
                                                PaymentCurrency = None
                                            }
                                    }
                        }

                    Expect.throws
                        (fun () -> Codec.encode HumanDocumentCodecs.document unbound |> ignore)
                        "A screen cannot invent an undeclared operation handle")
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

                    let original = Frames.create Api.operationExecute input |> required
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
