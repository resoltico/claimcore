module ClaimCore.Tests.EndpointRegistryTests

open ClaimCore.TestSupport

open ClaimCore.Web

open ClaimCore.Protocol

open System
open System.Text
open System.Security.Cryptography
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

let private encode value = WireWriter.bytes value
let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private accepted =
    Result.defaultWith (fun problem -> failtest (sprintf "Unexpected codec refusal: %A" problem))

let private id = Guid.Parse "10000000-0000-4000-8000-000000000001"

let private roundTrip codec value =
    Expect.equal
        (Codec.encode codec value |> Codec.decode 100000 codec)
        (Ok value)
        "The shared model crosses its codec unchanged."

let rec private inputMembers definitions =
    function
    | Schema.Ref name -> inputMembers definitions (Map.find name definitions)
    | Schema.Object(members, _) -> members |> List.map fst |> Set.ofList
    | _ -> failtest "Inventoried read inputs require an object schema."

let private registry () =
    let rows =
        Api.all
        |> List.map (fun endpoint -> endpoint.Token, endpoint.Method.Method, endpoint.Effect)


    let expected =
        Inventory.rows "endpoints"
        |> List.map (fun row ->
            row.GetProperty("token").GetString() |> nonNull,
            row.GetProperty("method").GetString() |> nonNull,
            row.GetProperty("effect").GetString() |> nonNull)

    Expect.equal
        (rows
         |> List.map (fun (token, method, effect) -> token, method, string effect)
         |> List.sort)
        (List.sort expected)
        "The registry contains exactly the independently inventoried methods and effects."

    for KeyValue(token, expected) in Inventory.readInputs do
        let route = Api.tryFind token |> Option.get

        Expect.equal
            (inputMembers route.InputDefinitions route.InputSchema)
            expected
            ("Read input inventory: " + token)


let private readReplies () =
    let codec = ReplyCodecs.readReply "BooleanReply" Codec.boolean

    for value in
        [
            Outcome.Succeeded true
            Outcome.Succeeded false
            Outcome.Rejected Refusal.InvalidCursor
            Outcome.Failed CoreFault.StoreUnavailable
        ] do
        roundTrip codec value

    for input in
        [
            "{\"outcome\":{\"kind\":\"CANCELLED\",\"data\":null}}"
            "{\"outcome\":{\"kind\":\"SUCCEEDED\",\"data\":true,\"message\":\"private\"}}"
        ] do
        Expect.isError
            (Codec.decode 10000 codec (bytes input))
            "Unadvertised outcomes and prose cannot enter an envelope."

let private receipt =
    {
        OperationId = (CaseFixtures.operationId (id))
        Position =
            {
                Sequence = CaseFixtures.sequence 1L
                EntryHash =
                    (String.replicate 64 "a"
                     |> System.Convert.FromHexString
                     |> EntryHash.ofBytes
                     |> Option.get)
            }
        RecordedAt = DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
        RecordedBy = (CaseFixtures.actorId (id))
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActorId = (CaseFixtures.actorId (id))
                    Act = AuthorityKind.Register
                }
    }

let private authorityReview =
    OperationReview.Authority
        {
            Target = None
            AccessScope = GrantScope.Installation
            Before =
                {
                    Enabled = false
                    Grants = []
                    Access = []
                }
            After =
                {
                    Enabled = true
                    Grants = []
                    Access = []
                }
        }

let private mutationReplies () =
    for outcome in
        [
            (Previewed.Reviewed >> Outcome.Succeeded) authorityReview
            (Previewed.AlreadyAccepted >> Outcome.Succeeded) receipt
            Outcome.Rejected(Refusal.Authority AuthorityRefusal.LastOwner)
            Outcome.Failed CoreFault.StoreIntegrityError
        ] do
        roundTrip
            ReplyCodecs.preview
            {
                PreviewReply.OperationId = id
                Outcome = outcome
            }

    for outcome in
        [
            ExecuteResult.Accepted receipt
            ExecuteResult.AlreadyAccepted receipt
            ExecuteResult.Rejected Refusal.IdempotencyConflict
            ExecuteResult.Failed CoreFault.StoreUnavailable
            ExecuteResult.OutcomeUnknown
        ] do
        roundTrip
            ReplyCodecs.execute
            {

                ExecuteReply.OperationId = id
                Outcome = outcome
            }

    let parsed =
        Codec.encode ReceiptCodecs.receipt receipt |> StrictJson.read 10000 |> accepted

    let scope = JsonValue.tryMember "subject" parsed |> Option.get

    Expect.equal
        (JsonValue.tryMember "kind" scope)
        (Some(JsonValue.String "AUTHORITY"))
        "Receipt scope owns its target fields."

    Expect.equal
        (JsonValue.tryMember "targetActorId" parsed)
        None
        "The target is nested under its scope."

let private structuralIdentity () =
    let document = OpenApi.document

    let expected =
        document |> CanonicalWriter.bytes |> SHA256.HashData |> Convert.ToHexStringLower

    Expect.equal
        ContractIdentity.value
        expected
        "The in-process identity is the canonical structural document hash."

    let text = encode document |> Encoding.UTF8.GetString

    let schemas =
        JsonValue.tryMember "components" document
        |> Option.bind (JsonValue.tryMember "schemas")
        |> Option.get

    let schemaText = encode schemas |> Encoding.UTF8.GetString

    Expect.isFalse
        (schemaText.Contains("\"description\"", StringComparison.Ordinal))
        "Shared schemas contain no prose."

    let parsed = Microsoft.OpenApi.OpenApiDocument.Parse(text, "json")

    Expect.isEmpty
        (nonNull parsed.Diagnostic).Errors
        "The independent OpenAPI parser admits the registry document."

let private hostFailures () =
    for reason in ClaimCore.Protocol.HostFailureReason.all do
        let expected = HostFailureCodecs.ofReason reason

        Expect.equal
            (HostFailureCodecs.encode reason |> HostFailureCodecs.decode)
            (Ok expected)
            "The minimal host payload round trips."

    let original = HostFailureCodecs.ofReason HostFailureReason.CompletedResponseFailed
    Expect.equal original.Phase None "The actual baseline's completed-response phase is preserved."

    let injected =
        JsonValue.Object
            [
                "hostFailure",
                JsonValue.Object
                    [
                        "code", JsonValue.String "INPUT_INVALID"
                        "phase", JsonValue.String "NOT_STARTED"
                        "message", JsonValue.String "private"
                    ]
            ]

    Expect.isError
        (injected |> encode |> HostFailureCodecs.decode)
        "Host failures refuse server prose."

[<Tests>]
let tests =
    testList
        "typed endpoint registry"
        [
            testCase "inventoried endpoints own method and mutation classification" registry
            testCase "one read envelope carries success, refusal and fault" readReplies
            testCase "preview and execution share receipt and refusal models" mutationReplies
            testCase "identity includes only the registry's structural OpenAPI" structuralIdentity
            testCase "host failures contain only code and phase" hostFailures
        ]

[<Tests>]
let handlerBindings =
    testCase "every closed actor endpoint exports its own typed handler" (fun () ->
        for actor in ActorEndpoint.all do
            let endpoint = ClaimCore.Web.ApiRoutes.route actor |> fun route -> route.Description

            Expect.equal
                (Api.identity endpoint)
                (EndpointId.Actor actor)
                "The handler's endpoint identity agrees with its exhaustive case")

[<Tests>]
let handlerCallTypes =
    testCase "handler calls and endpoint behavior retain nominal input and output types" (fun () ->
        let methodInfo =
            typeof<ClaimCore.Web.Route>.Assembly.GetTypes()
            |> Array.collect (fun kind ->
                kind.GetMethods(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Static))
            |> Array.find (fun methodInfo ->
                let parameters = methodInfo.GetParameters()

                methodInfo.Name = "bind"
                && parameters.Length = 2
                && parameters[0].ParameterType.IsGenericType
                && parameters[0].ParameterType.GetGenericTypeDefinition() =
                    typedefof<Endpoint<_, _>>)

        let parameters = methodInfo.GetParameters()
        let endpoint = parameters[0].ParameterType
        let endpointTypes = endpoint.GetGenericArguments()

        let correlation =
            (endpoint.GetProperty("Correlation") |> nonNull).PropertyType.GetGenericArguments()

        Expect.equal correlation[0] endpointTypes[0] "Correlation receives the typed input"

        Expect.equal
            correlation[1]
            typeof<Guid option>
            "Correlation returns an optional operation id"

        let unavailable =
            (endpoint.GetProperty("Unavailable") |> nonNull).PropertyType.GetGenericArguments()

        Expect.equal unavailable[0] endpointTypes[0] "Unavailable receives this exact input"
        Expect.equal unavailable[1] endpointTypes[1] "Unavailable returns this exact output"
        let arguments (kind: Type) = kind.GetGenericArguments()
        let invocation = parameters[1].ParameterType |> arguments
        let input = invocation[1] |> arguments
        Expect.equal input[0] endpointTypes[0] "Invocation receives this exact input"
        let cancellation = input[1] |> arguments
        let output = cancellation[1] |> arguments
        Expect.equal output[0] endpointTypes[1] "Invocation returns this exact output")
