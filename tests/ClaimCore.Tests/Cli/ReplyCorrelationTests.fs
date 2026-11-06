module ClaimCore.Tests.ReplyCorrelationTests

open System
open System.Text.Json.Nodes
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
        Api.operationPreview.Output.Write(
            PresentedPreviewReply.ofCore
                {
                    PreviewReply.OperationId = operation
                    Outcome = Outcome.Succeeded(Previewed.AlreadyAccepted accepted)
                }
        )
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

let private correlationReview family =
    ClaimCore.TestSupport.PositiveExamples.forEndpoint
        "wire-response-examples.json"
        "operation.preview"
    |> List.find (fun value ->
        let at path =
            path
            |> List.fold
                (fun value key -> value |> Option.bind (JsonValue.tryMember key))
                (Some value)

        at [ "outcome"; "data"; "review"; "subject"; "kind" ] = Some(JsonValue.String family))
    |> WireWriter.bytes
    |> System.Text.Encoding.UTF8.GetString
    |> JsonNode.Parse
    |> nonNull

let private jsonValue (node: JsonNode) =
    StrictJson.read Limits.MaximumResponseBytes (bytes (node.ToJsonString()))
    |> Result.defaultWith (fun _ -> failtest "Authored JSON")

let private reviewedIdentity () =
    let operation = "10000000-0000-4000-8000-000000000001"

    let input =
        (JsonNode.Parse
            """{"kind":"CASE","operationId":"10000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"UNIT-001"},"expectedRevision":0,"act":{"kind":"OPEN","values":{"incidentDate":"2026-01-01","incidentNotificationDate":"2026-01-01","incidentCountry":"LV","claimantName":"x","insurerName":"x","claimedAmount":"0","claimedCurrency":"EUR"}}}"""
         |> nonNull)

    let reply = correlationReview "CASE"
    reply["operationId"] <- System.Text.Json.Nodes.JsonValue.Create operation

    let subject =
        (((reply["outcome"] |> nonNull)["data"] |> nonNull)["review"] |> nonNull)["subject"]
        |> nonNull

    subject["before"] <- null
    (subject["after"] |> nonNull)["caseId"] <- System.Text.Json.Nodes.JsonValue.Create operation

    (subject["after"] |> nonNull)["caseReference"] <-
        System.Text.Json.Nodes.JsonValue.Create "UNIT-001"

    (subject["after"] |> nonNull)["revision"] <- System.Text.Json.Nodes.JsonValue.Create 1

    let assertMatches expected label =
        Expect.isOk
            (Api.operationPreview.Output.Read "$" (jsonValue reply))
            "Mutation stays structurally admitted"

        Expect.equal
            (ReplyCorrelation.matches "operation.preview" (jsonValue input) (jsonValue reply))
            expected
            label

        let invocation =
            JsonValue.Object
                [ "endpoint", JsonValue.String "operation.preview"; "input", jsonValue input ]
            |> WireWriter.bytes
            |> Frames.admit
            |> Result.defaultWith (fun _ ->
                failtest "Independent authored request remains structural")

        let actual = exchange invocation (jsonValue reply)

        Expect.equal
            actual.ExitCode
            (if expected then 0 else 2)
            "Actual HTTP admission rejects mismatched review"

        if not expected then
            Expect.equal
                actual.Frame.Delivery
                DeliveryState.Uncertain
                "Mismatched review never enables confirmation"

    assertMatches true "OPEN is correlated without looking up an absent case"
    (subject["after"] |> nonNull)["revision"] <- System.Text.Json.Nodes.JsonValue.Create 999
    assertMatches false "A different proposed revision cannot enable confirmation"
    (subject["after"] |> nonNull)["revision"] <- System.Text.Json.Nodes.JsonValue.Create 1

    (subject["after"] |> nonNull)["caseId"] <-
        System.Text.Json.Nodes.JsonValue.Create "90000000-0000-4000-8000-000000000099"

    assertMatches false "OPEN identity derives from the actual operation"
    let before = (subject["after"] |> nonNull).DeepClone()
    before["revision"] <- System.Text.Json.Nodes.JsonValue.Create 1
    subject["before"] <- before
    (subject["after"] |> nonNull)["revision"] <- System.Text.Json.Nodes.JsonValue.Create 2
    input["expectedRevision"] <- System.Text.Json.Nodes.JsonValue.Create 1
    input["act"] <- (JsonNode.Parse """{"kind":"CLOSE"}""" |> nonNull)
    assertMatches true "Existing case before and proposed versions are correlated"
    before["revision"] <- System.Text.Json.Nodes.JsonValue.Create 8
    assertMatches false "A foreign before revision cannot enable confirmation"
    before["revision"] <- System.Text.Json.Nodes.JsonValue.Create 1
    before["caseReference"] <- System.Text.Json.Nodes.JsonValue.Create "FOREIGN"
    assertMatches false "A foreign before reference cannot enable confirmation"

let private reviewedAuthorityIdentity () =
    let reply = correlationReview "AUTHORITY"

    let subject =
        (((reply["outcome"] |> nonNull)["data"] |> nonNull)["review"] |> nonNull)["subject"]
        |> nonNull

    let input =
        (JsonNode.Parse
            """{"kind":"AUTHORITY","operationId":"10000000-0000-4000-8000-000000000001","target":{"kind":"HUMAN","issuer":"https://issuer.example/","subject":"person"},"act":{"kind":"ENABLE"}}"""
         |> nonNull)

    reply["operationId"] <- (input["operationId"] |> nonNull).DeepClone()
    // Use the complete independent actor shape from an existing actor page.
    let actor =
        ClaimCore.TestSupport.PositiveExamples.forEndpoint
            "wire-response-examples.json"
            "authority.list"
        |> List.pick (fun value ->
            match
                JsonValue.tryMember "outcome" value
                |> Option.bind (JsonValue.tryMember "data")
                |> Option.bind (JsonValue.tryMember "items")
            with
            | Some(JsonValue.Array(head :: _)) -> Some head
            | _ -> None)
        |> WireWriter.bytes
        |> System.Text.Encoding.UTF8.GetString
        |> JsonNode.Parse
        |> nonNull

    actor["principal"] <- (input["target"] |> nonNull).DeepClone()
    subject["target"] <- actor
    subject["effects"] <- (JsonNode.Parse """[{"kind":"ENABLE"}]""" |> nonNull)

    let assertMatches expected label =
        Expect.isOk
            (Api.operationPreview.Output.Read "$" (jsonValue reply))
            "Authority mutation stays structurally admitted"

        Expect.equal
            (ReplyCorrelation.matches "operation.preview" (jsonValue input) (jsonValue reply))
            expected
            label

        let invocation =
            JsonValue.Object
                [ "endpoint", JsonValue.String "operation.preview"; "input", jsonValue input ]
            |> WireWriter.bytes
            |> Frames.admit
            |> Result.defaultWith (fun _ ->
                failtest "Independent authored request remains structural")

        let actual = exchange invocation (jsonValue reply)

        Expect.equal
            actual.ExitCode
            (if expected then 0 else 2)
            "Actual HTTP admission rejects mismatched review"

        if not expected then
            Expect.equal
                actual.Frame.Delivery
                DeliveryState.Uncertain
                "Mismatched review never enables confirmation"

    assertMatches true "The authored existing account is correlated"

    (actor["principal"] |> nonNull)["subject"] <-
        System.Text.Json.Nodes.JsonValue.Create "another-person"

    assertMatches false "Another account cannot enable confirmation"
    subject["target"] <- null
    assertMatches false "An existing-account action requires the disclosed target principal"

    input["act"] <-
        (JsonNode.Parse """{"kind":"REGISTER","profile":{"displayName":"Person"}}"""
         |> nonNull)

    subject["effects"] <-
        (JsonNode.Parse
            """[{"kind":"REGISTER","principal":{"kind":"HUMAN","issuer":"https://issuer.example/","subject":"person"}},{"kind":"SET_PROFILE","profile":{"displayName":"Person","contact":null}}]"""
         |> nonNull)

    assertMatches true "New account identity is owned by its registration effect"

    (((subject["effects"] |> nonNull)[0] |> nonNull)["principal"] |> nonNull)["subject"] <-
        System.Text.Json.Nodes.JsonValue.Create "foreign"

    assertMatches false "A foreign registration effect cannot enable confirmation"

    (((subject["effects"] |> nonNull)[0] |> nonNull)["principal"] |> nonNull)["subject"] <-
        System.Text.Json.Nodes.JsonValue.Create "person"

    subject["effects"] <- (JsonNode.Parse """[{"kind":"ENABLE"}]""" |> nonNull)
    assertMatches false "A different action cannot masquerade as registration"

let private createdReceipts () =
    let operation = CliReplyFixtures.id

    let foreign =
        CaseFixtures.actorId (Guid.Parse "90000000-0000-4000-8000-000000000099")

    let assertResponse invocation value expected =
        Expect.isOk
            ((Frames.endpoint invocation).NormalizeOutput value)
            "Counterexample remains structurally admitted"

        let actual = exchange invocation value

        Expect.equal
            (actual.ExitCode = 0)
            expected
            "Actual HTTP receipt admission binds deterministic created identity"

        if not expected then
            Expect.equal
                actual.Frame.Delivery
                DeliveryState.Uncertain
                "Foreign receipt cannot settle the frozen operation"

    for endpoint in [ "operation.execute"; "operation.preview"; "operation.observe" ] do
        let invocation = frame endpoint
        let matching = body invocation
        assertResponse invocation matching true

        let root =
            WireWriter.bytes matching
            |> System.Text.Encoding.UTF8.GetString
            |> JsonNode.Parse
            |> nonNull

        let data = ((root["outcome"] |> nonNull)["data"] |> nonNull)

        (data["subject"] |> nonNull)["caseId"] <-
            System.Text.Json.Nodes.JsonValue.Create(ActorId.format foreign)

        assertResponse invocation (jsonValue root) false

    for act in
        [
            AuthorityAct.Register Inputs.actorProfile
            AuthorityAct.Admit
                {
                    Profile = Inputs.actorProfile
                    Grants =
                        [
                            {
                                Role = Role.CaseReader
                                Scope = GrantTarget.Installation
                            }
                        ]
                }
        ] do
        let request =
            { CliReplyFixtures.authorityRequest with
                Act = act
            }

        let invocation =
            Frames.create Api.operationExecute (OperationInput.Authority request)
            |> Result.defaultWith (fun _ -> failtest "Authored admission input")

        let target = ActorId.ofOperationId (CaseFixtures.operationId operation)

        for disclose in [ false; true ] do
            let accepted id =
                { CliReplyFixtures.receipt with
                    OperationId = CaseFixtures.operationId operation
                    Subject =
                        ReceiptSubject.Authority
                            {
                                Act = AuthorityAct.kind act
                                TargetActor =
                                    if disclose then
                                        AuthorityTargetDisclosure.Disclosed
                                            { CliReplyFixtures.receipt.RecordedBy with
                                                ActorId = id
                                            }
                                    else
                                        AuthorityTargetDisclosure.Withheld id
                                Effects =
                                    [
                                        {
                                            Act = AuthorityKind.Register
                                            Position = CliReplyFixtures.receipt.Position
                                        }
                                    ]
                            }
                }

            let reply id =
                Api.operationExecute.Output.Write
                    {
                        OperationId = operation
                        Outcome = ExecuteResult.Accepted(accepted id)
                    }

            assertResponse invocation (reply target) true
            assertResponse invocation (reply foreign) false

[<Tests>]
let tests =
    testList
        "client reply correlation"
        [
            testCase
                "created case and account receipts retain deterministic operation identity"
                createdReceipts
            testCase
                "case preview binds both snapshots to the authored identity and revision"
                reviewedIdentity
            testCase
                "authority preview binds target and registration effects to the authored principal"
                reviewedAuthorityIdentity
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
            testCase "grants correlation follows the related actor reference" (fun () ->
                let invocation = frame "authority.grants"
                let bytes = samples "authority.grants" |> List.head |> correlatedBody invocation

                let admitted =
                    Codec.decode Limits.MaximumResponseBytes Api.authorityGrants.Output bytes
                    |> Result.defaultWith (fun error ->
                        failtestf "Positive grants reply: %A" error)

                let page =
                    match admitted with
                    | Outcome.Succeeded page -> page
                    | other -> failtestf "Expected grants page: %A" other

                Expect.equal
                    (exchange invocation (Api.authorityGrants.Output.Write admitted)).ExitCode
                    0
                    "The matching nested actor ID is admitted"

                let wrong =
                    CaseFixtures.actorId (Guid.Parse "90000000-0000-4000-8000-000000000099")

                let mismatched =
                    Outcome.Succeeded
                        { page with
                            Actor =
                                { page.Actor with
                                    Actor =
                                        { page.Actor.Actor with
                                            ActorId = wrong
                                        }
                                }
                        }
                    |> Api.authorityGrants.Output.Write

                Expect.isOk
                    ((Frames.endpoint invocation).NormalizeOutput mismatched)
                    "The negative control remains schema-valid"

                Expect.isTrue
                    ((exchange invocation mismatched).ExitCode <> 0)
                    "A reply about another actor is rejected")
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
