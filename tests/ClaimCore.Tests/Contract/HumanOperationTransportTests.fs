module ClaimCore.Tests.HumanOperationTransportTests

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

let rec private containsScreen =
    function
    | JsonValue.Object members ->
        List.exists (fst >> (=) "regions") members
        || List.exists (snd >> containsScreen) members
    | JsonValue.Array items -> List.exists containsScreen items
    | _ -> false

let private exercise endpoint unavailable describe input core =
    task {
        let mutable invoked = 0
        let mutable completed = 0

        let route =
            ClaimCore.Web.Route.represented
                endpoint
                unavailable
                describe
                (fun _ -> None)
                (fun _ received _ ->
                    invoked <- invoked + 1
                    Expect.equal received input "Output selection cannot change the authored input"
                    Task.FromResult core)

        let prepared = route.Prepare(endpoint.Input.Write input) |> required
        let data = Frames.create endpoint input |> required
        let human = Frames.withRepresentation Representation.Human data

        Expect.equal
            (Frames.inputBytes human)
            (Frames.inputBytes data)
            "Identical frozen business command"

        Expect.equal
            (Frames.operationId human)
            (Frames.operationId data)
            "Identical acceptance identity"

        for representation, frame in [ Representation.Data, data; Representation.Human, human ] do
            let! reply =
                prepared.Invoke representation (Some services) CancellationToken.None (fun () ->
                    completed <- completed + 1)

            Expect.equal invoked completed "One completion for each actual service result"

            Expect.equal
                (JsonValue.tryMember "representation" reply.Wire)
                (Some(JsonValue.String(Representation.token representation)))
                "Selected representation"

            CliReply.result frame reply.Wire |> required |> ignore

            let other =
                Frames.withRepresentation
                    (if representation = Representation.Human then
                         Representation.Data
                     else
                         Representation.Human)
                    frame

            Expect.isError
                (CliReply.result other reply.Wire)
                "A valid opposite representation cannot settle this request"

            if representation = Representation.Data then
                Expect.isFalse (containsScreen reply.Wire) "No Screen in DATA"
            else
                let outcome = JsonValue.tryMember "outcome" reply.Wire |> Option.get
                Expect.isNone (JsonValue.tryMember "data" outcome) "No raw DATA result in HUMAN"

        Expect.equal
            invoked
            2
            "One use-case invocation per separate observation, never two projections' queries"
    }

[<Tests>]
let tests =
    testList
        "human operation transport"
        [
            testCase
                "OpenAPI declares actor-only transport selection independently of business input"
                (fun () ->
                    let memberValue name value =
                        JsonValue.tryMember name value |> Option.get

                    let paths = memberValue "paths" OpenApi.document

                    for endpoint in Api.all do
                        let operation =
                            paths
                            |> memberValue (Api.path endpoint)
                            |> memberValue ((Api.method endpoint).Method.ToLowerInvariant())

                        let parameters =
                            match memberValue "parameters" operation with
                            | JsonValue.Array values -> values
                            | _ -> failtest "Parameter array required"

                        let selected =
                            parameters
                            |> List.filter (fun value ->
                                JsonValue.tryMember "name" value =
                                    Some(JsonValue.String "ClaimCore-Representation"))

                        match endpoint.Id, selected with
                        | EndpointId.Host _, [] -> ()
                        | EndpointId.Actor _, [ header ] ->
                            Expect.equal
                                (memberValue "required" header)
                                (JsonValue.Boolean false)
                                "Missing header is valid"

                            let schema = memberValue "schema" header

                            Expect.equal
                                (memberValue "default" schema)
                                (JsonValue.String "DATA")
                                "Normative default"

                            Expect.equal
                                (memberValue "enum" schema)
                                (JsonValue.Array
                                    [ JsonValue.String "HUMAN"; JsonValue.String "DATA" ])
                                "Canonical transport choices"
                        | _ -> failtest "Every actor and no host declares representation selection"

                    let authored = Api.operationPreview.Input.Write ResponseExamples.previewInput

                    Expect.isNone
                        (JsonValue.tryMember "representation" authored)
                        "Selection never becomes business input")

            testCaseAsync
                "preview execute and lookup select one projection of one service result"
                (async {
                    do!
                        exercise
                            Api.operationPreview
                            Api.operationPreviewUnavailable
                            HumanReplies.preview
                            ResponseExamples.previewInput
                            ResponseExamples.preview
                        |> Async.AwaitTask

                    let input = ResponseExamples.previewInput

                    let accepted =
                        {
                            ExecuteReply.OperationId = OperationInput.operationId input
                            Outcome = ExecuteResult.Accepted ResponseExamples.receipt
                        }

                    do!
                        exercise
                            Api.operationExecute
                            Api.operationExecuteUnavailable
                            (fun _ reply -> HumanReplies.execute reply)
                            input
                            accepted
                        |> Async.AwaitTask

                    let lookup =
                        {
                            OperationLookup.OperationId =
                                OperationId.value ResponseExamples.receipt.OperationId
                        }

                    let found =
                        {
                            OperationReply.OperationId = lookup.OperationId
                            Outcome =
                                Outcome.Succeeded(
                                    OperationLookupResult.Accepted(ResponseExamples.receipt, true)
                                )
                        }

                    do!
                        exercise
                            Api.operationGet
                            Api.operationGetUnavailable
                            (fun _ reply -> HumanReplies.lookup reply)
                            lookup
                            found
                        |> Async.AwaitTask
                })

            testCase
                "both representations admit every operation outcome without mixed success payloads"
                (fun () ->
                    for endpoint, expected in
                        [
                            "operation.preview",
                            set [ "REVIEWED"; "ALREADY_ACCEPTED"; "REJECTED"; "FAILED" ]
                            "operation.execute",
                            set
                                [
                                    "ACCEPTED"
                                    "ALREADY_ACCEPTED"
                                    "REJECTED"
                                    "FAILED"
                                    "OUTCOME_UNKNOWN"
                                ]
                            "operation.get",
                            set [ "ACCEPTED"; "PENDING"; "NOT_ACCEPTED"; "REJECTED"; "FAILED" ]
                        ] do
                        let route = Api.tryFind endpoint |> Option.get
                        let samples = ResponseExamples.samples endpoint

                        for representation in [ "HUMAN"; "DATA" ] do
                            let selected =
                                samples
                                |> List.map (
                                    StrictJson.read Limits.MaximumResponseBytes >> required
                                )
                                |> List.filter (fun value ->
                                    JsonValue.tryMember "representation" value =
                                        Some(JsonValue.String representation))

                            let kinds =
                                selected
                                |> List.map (fun value ->
                                    JsonValue.tryMember "outcome" value
                                    |> Option.bind (JsonValue.tryMember "kind")
                                    |> Option.get)
                                |> Set.ofList

                            Expect.equal
                                kinds
                                (expected |> Set.map JsonValue.String)
                                "Every declared outcome has a real fixture in each mode"

                            for value in selected do
                                route.ReadOutput value |> required |> ignore

                                if representation = "DATA" then
                                    Expect.isFalse
                                        (containsScreen value)
                                        "DATA never embeds a Screen"
                                else
                                    let outcome = JsonValue.tryMember "outcome" value |> Option.get

                                    Expect.isNone
                                        (JsonValue.tryMember "data" outcome)
                                        "HUMAN never embeds machine snapshots")

            testCase
                "actual native review rendering uses opaque supplied facts rather than the frozen input"
                (fun () ->
                    let input =
                        match ResponseExamples.previewInput with
                        | OperationInput.Case value ->
                            OperationInput.Case
                                { value with
                                    Act = CaseAct.Void "CLIENT_INPUT_BAIT"
                                }
                        | _ -> failtest "Case fixture required"

                    let core =
                        match ResponseExamples.preview.Outcome with
                        | Outcome.Succeeded(Previewed.Reviewed value) -> value
                        | _ -> failtest "Review fixture required"

                    let review = HumanReviewDocument.ofCore input core

                    let opaque =
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
                                            (PresentedValue.Text "CORE_REVIEW_CANARY🪙")
                                    ]
                            ]

                    let review =
                        { review with
                            Document = { review.Document with Screen = opaque }
                            Presentation = { Changes = []; Unchanged = [] }
                        }

                    let reply =
                        RepresentationReply.Human
                            {
                                HumanOperationReply.OperationId = OperationInput.operationId input
                                Outcome = HumanPreviewResult.Reviewed review
                            }

                    let frame =
                        Frames.create Api.operationPreview input
                        |> required
                        |> Frames.withRepresentation Representation.Human

                    let admitted =
                        CliReply.result frame (Api.operationPreview.Output.Write reply) |> required

                    let text =
                        ReplyText.bytesFor "en" (Some frame) admitted.Frame
                        |> Encoding.UTF8.GetString

                    Expect.stringContains
                        text
                        "CORE_REVIEW_CANARY🪙"
                        "The actual CLI uses the supplied core presentation"

                    Expect.isFalse
                        (text.Contains "CLIENT_INPUT_BAIT")
                        "The command is not a read-only display source")

            testCaseAsync
                "confirmed work remains completed when HUMAN projection fails"
                (async {
                    let mutable completed = false
                    let mutable invoked = 0
                    let input = ResponseExamples.previewInput

                    let core =
                        {
                            ExecuteReply.OperationId = OperationInput.operationId input
                            Outcome = ExecuteResult.Accepted ResponseExamples.receipt
                        }

                    let route =
                        ClaimCore.Web.Route.represented
                            Api.operationExecute
                            Api.operationExecuteUnavailable
                            (fun _ _ -> failwith "Synthetic projection failure")
                            (fun _ -> None)
                            (fun _ _ _ ->
                                invoked <- invoked + 1
                                Task.FromResult core)

                    let prepared = route.Prepare(Api.operationExecute.Input.Write input) |> required

                    let! result =
                        prepared.Invoke
                            Representation.Human
                            (Some services)
                            CancellationToken.None
                            (fun () -> completed <- true)
                        |> Async.AwaitTask
                        |> Async.Catch

                    match result with
                    | Choice1Of2 _ -> failtest "Projection failure control must fail"
                    | Choice2Of2 _ -> ()

                    Expect.equal invoked 1 "Accepted use case is never repeated"

                    Expect.isTrue
                        completed
                        "Phase-aware host failure can report confirmed work without an uncertain redispatch"
                })
        ]
