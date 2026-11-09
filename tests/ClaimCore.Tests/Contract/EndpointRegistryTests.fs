module ClaimCore.Tests.EndpointRegistryTests

open ClaimCore.TestSupport

open ClaimCore.Web

open ClaimCore.Wire
open ClaimCore.Presentation

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

let private id = Guid.Parse "10000000-0000-7000-8000-000000000001"

let private roundTrip codec value =
    Expect.equal
        (Codec.encode codec value |> Codec.decode 100000 codec)
        (Ok value)
        "The shared model crosses its codec unchanged."

let private registry () =
    let tokens = Api.all |> List.map _.Token
    Expect.equal (Set.ofList tokens).Count tokens.Length "Endpoint tokens are unique"

    for endpoint in Api.all do
        Expect.equal
            endpoint.Method.Method
            (if endpoint.Token = "session" then "GET" else "POST")
            "Only the session query uses GET"

        let expectedEffect =
            match endpoint.Token with
            | "operation.execute" -> EndpointEffect.LedgerExecution
            | "session.logout"
            | "authority.requests.dismiss" -> EndpointEffect.OperationalWrite
            | _ -> EndpointEffect.Read

        Expect.equal endpoint.Effect expectedEffect "Only declared mutations write state"

let private readReplies () =
    let codec = ReplyCodecs.readReply "BooleanReply" Codec.boolean

    for value in
        [
            Outcome.Succeeded(Presented.create (fun _ -> Screen.create []) true)
            Outcome.Succeeded(Presented.create (fun _ -> Screen.create []) false)
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

let private receipt: OperationReceipt =
    {
        OperationId = (CaseFixtures.operationId (id))
        Position =
            {
                Sequence = CaseFixtures.sequence 2L
                EntryHash =
                    (String.replicate 64 "a"
                     |> System.Convert.FromHexString
                     |> EntryHash.ofBytes
                     |> Option.get)
            }
        RecordedAt = DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
        Basis =
            {
                ObservedAt = DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
                BusinessDate = DateOnly(2026, 10, 22)
                TimeZoneId = "Etc/UTC"
            }
        RecordedBy = ClaimCore.TestSupport.Inputs.actorReference ((CaseFixtures.actorId (id)))
        Subject =
            ReceiptSubject.Authority
                {
                    TargetActor = AuthorityTargetDisclosure.Withheld(CaseFixtures.actorId id)
                    Act = AuthorityKind.Register
                    Effects =
                        [
                            {
                                AuthorityReceiptEffect.Act = AuthorityKind.Register
                                Position =
                                    LedgerPosition.create
                                        (CaseFixtures.sequence 1L)
                                        (String.replicate 64 "a"
                                         |> Convert.FromHexString
                                         |> EntryHash.ofBytes
                                         |> Option.get)
                            }
                            {
                                AuthorityReceiptEffect.Act = AuthorityKind.SetProfile
                                Position =
                                    LedgerPosition.create
                                        (CaseFixtures.sequence 2L)
                                        (String.replicate 64 "a"
                                         |> Convert.FromHexString
                                         |> EntryHash.ofBytes
                                         |> Option.get)
                            }
                        ]
                }
    }

let private authorityReview =
    OperationReview.Authority
        {
            Basis = ClaimCore.TestSupport.Inputs.workBasis
            Effects = [ AuthorityReviewEffect.Enable ]
            Target = None
            AccessScope = GrantScope.Installation
            Before =
                {
                    Enabled = false
                    Profile = None
                    Grants = []
                    Access = []
                }
            After =
                {
                    Enabled = true
                    Profile = None
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
            (PresentedPreviewReply.ofCore
                None
                {
                    PreviewReply.OperationId = id
                    Outcome = outcome
                })

    for outcome in
        [
            PresentedExecuteResult.Accepted(Presented.create ReceiptScreen.view receipt)
            PresentedExecuteResult.AlreadyAccepted(Presented.create ReceiptScreen.view receipt)
            PresentedExecuteResult.Rejected Refusal.IdempotencyConflict
            PresentedExecuteResult.Failed CoreFault.StoreUnavailable
            PresentedExecuteResult.OutcomeUnknown
        ] do
        roundTrip
            ReplyCodecs.execute
            {

                PresentedExecuteReply.OperationId = id
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

    let rec hasProse =
        function
        | JsonValue.Object fields ->
            fields
            |> List.exists (fun (name, value) ->
                (name = "description"
                 && (match value with
                     | JsonValue.String _ -> true
                     | _ -> false))
                || hasProse value)
        | JsonValue.Array values -> List.exists hasProse values
        | _ -> false

    Expect.isFalse
        (hasProse schemas)
        "Schema annotations contain no prose; data property names remain structural."

    Expect.isTrue
        (hasProse (JsonValue.Object["description", JsonValue.String "prose"]))
        "A schema description is detected"

    Expect.isFalse
        (hasProse (
            JsonValue.Object["properties",
                             JsonValue.Object["description",
                                              JsonValue.Object["type", JsonValue.String "string"]]]
        ))
        "A description data field is retained"

    let parsed = Microsoft.OpenApi.OpenApiDocument.Parse(text, "json")

    Expect.isEmpty
        (nonNull parsed.Diagnostic).Errors
        "The independent OpenAPI parser admits the registry document."

let private hostFailures () =
    for reason in ClaimCore.Wire.HostFailureReason.all do
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
            testCase "endpoints own unique names, methods and mutation classification" registry
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
            (endpoint.GetProperty("Correlation") |> nonNull).PropertyType.GetGenericArguments()[0]

        Expect.equal
            (correlation.GetGenericTypeDefinition())
            typedefof<OperationEcho<_, _>>
            "The optional correlation carries a typed request/reply pair"

        for name, expected in [ "Request", endpointTypes[0]; "Reply", endpointTypes[1] ] do
            let functionTypes =
                (correlation.GetProperty(name) |> nonNull).PropertyType.GetGenericArguments()

            Expect.equal functionTypes[0] expected "Each side keeps its nominal endpoint type"
            Expect.equal functionTypes[1] typeof<Guid> "Each side supplies only the operation ID"

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
