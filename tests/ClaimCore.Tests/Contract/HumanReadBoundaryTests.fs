module ClaimCore.Tests.HumanReadBoundaryTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private required value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private services =
    ClaimCore.Tests.ReadFixtures.services
        ClaimCore.Tests.ReadFixtures.installation
        ClaimCore.Tests.ReadFixtures.calendar
        ClaimCore.Tests.ReadFixtures.owner

[<Tests>]
let tests =
    testList
        "human read transport boundary"
        [
            testCase
                "section controls have one visible placement and form context preserves the current date"
                (fun () ->
                    let original = ResponseExamples.caseRead
                    let amend = TaskId.ofLiteral "case.amendRegistration"
                    let decide = TaskId.ofLiteral "case.decide"
                    let payment = TaskId.ofLiteral "case.recordPayment"
                    let offered = [ amend; decide; payment ]

                    let blocked =
                        original.Advice.Blocked
                        |> List.filter (fun item -> not (List.contains item.Act offered))

                    let available editable : Availability =
                        {
                            Seeds = Map.empty
                            Selections = Set.empty
                            Editable = editable
                            Choices = Map.empty
                            Applicable = true
                            Blocked = None
                            Recommendation = None
                        }

                    let binding =
                        Binding.Case(
                            CaseSelector.Id original.Snapshot.CaseId,
                            original.Snapshot.Revision
                        )

                    let date = TaskInputId.Target(InputTarget.Field BusinessField.PaymentDate)

                    let advice =
                        CaseAdvice.create offered None HistoryAccess.None blocked
                        |> required
                        |> CaseAdvice.withOffers
                            [
                                TaskOffer.Available(amend, binding, available [])
                                TaskOffer.Available(decide, binding, available [])
                                TaskOffer.Available(
                                    payment,
                                    binding,
                                    available
                                        [
                                            date
                                            TaskInputId.Target(
                                                InputTarget.Field BusinessField.PaidAmount
                                            )
                                            TaskInputId.Target(
                                                InputTarget.Field BusinessField.PaidCurrency
                                            )
                                        ]
                                )
                            ]
                        |> CaseAdvice.withRecommendation (Some decide)
                        |> required

                    let document = HumanCaseDocument.ofCore { original with Advice = advice }

                    let visible =
                        document.Actions
                        @ (document.Screen.Regions
                           |> List.filter (fun region -> region.Role <> RegionRole.Context)
                           |> List.collect _.Actions)

                    Expect.isEmpty
                        (visible
                         |> List.countBy _.Task
                         |> List.filter (fun (_, count) -> count <> 1))
                        "A control has one visible location"

                    let section role =
                        document.Screen.Regions |> List.find (fun region -> region.Role = role)

                    Expect.equal
                        ((section RegionRole.Facts).Actions |> List.map _.Task)
                        [ amend ]
                        "Claim details own their update control"

                    Expect.equal
                        ((section RegionRole.Recommendation).Actions |> List.map _.Task)
                        [ decide ]
                        "The recommendation has one authoritative location"

                    Expect.isEmpty
                        (section RegionRole.Payments).Actions
                        "The recommended action is not repeated in its section"

                    let context = (section RegionRole.Context).Actions |> List.exactlyOne

                    Expect.equal
                        context.Task
                        payment
                        "The associated form context carries the selected operation"

                    Expect.equal
                        context.Seeds[date]
                        (InputValue.Text "2026-08-31")
                        "Core supplies the observed business date"

                    let contextOnly =
                        ScreenEntries.value
                            "formContext"
                            "label.details"
                            Prominence.Primary
                            (PresentedValue.Text "FORM_CONTEXT_CANARY")

                    let regions =
                        document.Screen.Regions
                        |> List.map (fun region ->
                            if region.Role = RegionRole.Context then
                                { region with
                                    Content = RegionContent.Entries [ contextOnly ]
                                }
                            else
                                region)

                    let document =
                        { document with
                            Screen = Screen.withRegions regions document.Screen
                        }

                    let rendered =
                        ViewText.renderDocument false (RenderingContext.forLanguage "en") document
                        |> String.concat "\n"

                    Expect.stringContains
                        rendered
                        "Update claim details"
                        "Native text lists the section's supplied control"

                    Expect.stringContains
                        rendered
                        "Record payment decision"
                        "Native text lists the recommendation"

                    Expect.isFalse
                        (rendered.Contains "FORM_CONTEXT_CANARY")
                        "Form context is not a second current-fact display")
            testCase
                "the actor router rejects a protected query bound to an unchecked handler"
                (fun () ->
                    let unchecked =
                        ClaimCore.Server.Route.representedRead
                            Api.caseGet
                            HumanCaseDocument.ofCore
                            (fun _ _ _ ->
                                Task.FromResult(Outcome.Succeeded ResponseExamples.caseRead))

                    let endpoint = ActorEndpoint.Query QueryTaskId.CaseGet

                    Expect.throws
                        (fun () ->
                            ClaimCore.Server.ApiRoutes.requireClassification endpoint unchecked
                            |> ignore)
                        "A handler that can render a raw outcome cannot serve the protected route"

                    let protectedRoute = ClaimCore.Server.ApiRoutes.route endpoint

                    Expect.isTrue
                        protectedRoute.Protected
                        "The production route requires an application release")
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
                                Target = CaseSelector.Reference "CC-55KW-SSJ4-8HMS-G1BH-73"
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
                            [ BusinessField.PolicyNumber, "Input bait must not display" ]
                            DomainInputs.registration
                        |> required

                    let action =
                        TaskAffordances.offered
                            (TaskId.ofLiteral "case.amendRegistration")
                            (Some(
                                Binding.Case(
                                    CaseSelector.Id ResponseExamples.snapshot.CaseId,
                                    ResponseExamples.snapshot.Revision
                                )
                            ))
                            (RegistrationInput.values bait |> TaskAffordances.fields)
                            (TaskAffordances.authored (TaskId.ofLiteral "case.amendRegistration"))
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

                    let text =
                        ReplyText.bytes (RenderingContext.forLanguage "en") frame
                        |> Encoding.UTF8.GetString

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
                        TaskAffordances.offered
                            (TaskId.ofLiteral "case.amendRegistration")
                            (Some(
                                Binding.Case(
                                    CaseSelector.Id ResponseExamples.snapshot.CaseId,
                                    ResponseExamples.snapshot.Revision
                                )
                            ))
                            (RegistrationInput.values DomainInputs.registration
                             |> TaskAffordances.fields)
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
                                (Some(Binding.PeopleNew Revision.zero))
                                action.Seeds
                                []
                                Map.empty
                                None
                            |> ignore)
                        "An operation cannot acquire another subject's binding")
            testCase
                "native selection is correlated without entering frozen business bytes"
                (fun () ->
                    let input =
                        Submissions.create
                            "case.close"
                            (OperationId.value ResponseExamples.receipt.OperationId)
                            (Binding.Case(
                                CaseSelector.Reference "CC-55KW-SSJ4-8HMS-G1BH-73",
                                Revision.first
                            ))
                            (Submissions.fields [ BusinessField.Outcome, "OTHER" ])

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
                                Target = CaseSelector.Reference "CC-55KW-SSJ4-8HMS-G1BH-73"
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
