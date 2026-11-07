module ClaimCore.Tests.CliTransportFixture

open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli

let bytes (text: string) = Encoding.UTF8.GetBytes text

let private examples = ClaimCore.TestSupport.PositiveExamples.forEndpoint

let samples endpoint =
    examples "examples/responses.json" endpoint |> List.map WireWriter.bytes

let frame endpoint =
    ClaimCore.Json.JsonValue.Object
        [
            "endpoint", ClaimCore.Json.JsonValue.String endpoint
            "input", examples "examples/requests.json" endpoint |> List.head
        ]
    |> WireWriter.bytes
    |> Frames.admit
    |> Result.defaultWith (fun _ -> failwith "Fixture frame refused.")

let authorityFrame () =
    ClaimCore.Json.JsonValue.Object
        [
            "endpoint", ClaimCore.Json.JsonValue.String "operation.execute"
            "input",
            Api.operationExecute.Input.Write(
                OperationInput.Authority CliReplyFixtures.authorityRequest
            )
        ]
    |> WireWriter.bytes
    |> Frames.admit
    |> Result.defaultWith (fun _ -> failwith "Authority fixture refused.")

let frameEndpoint invocation = Frames.endpoint invocation
let frameBytes invocation = Frames.encodeInvocation invocation

let private at path value =
    path
    |> List.fold (fun value name -> value |> Option.bind (JsonValue.tryMember name)) (Some value)

let private memberText name value =
    JsonValue.tryMember name value
    |> Option.bind (function
        | JsonValue.String text -> Some text
        | _ -> None)

let private replace name value =
    function
    | JsonValue.Object fields ->
        JsonValue.Object((name, value) :: (fields |> List.filter (fst >> (<>) name)))
    | other -> other

let rec private update path value root =
    match path with
    | [] -> value
    | name :: rest ->
        match JsonValue.tryMember name root with
        | Some child -> replace name (update rest value child) root
        | None -> root

/// Mock transport identities derive from the invocation; business example values stay authored.
let correlatedBody invocation bytes =
    let root =
        StrictJson.read Limits.MaximumResponseBytes bytes
        |> Result.defaultWith (fun _ -> failtest "Positive mock JSON")

    let input = Frames.input invocation
    let endpoint = (Frames.endpoint invocation).Token

    let tag =
        at [ "outcome"; "kind" ] root
        |> Option.bind (function
            | JsonValue.String value -> Some value
            | _ -> None)

    let echoed =
        Frames.operationId invocation
        |> Option.map (fun id -> replace "operationId" (JsonValue.String(id.ToString("D"))) root)
        |> Option.defaultValue root

    let receipt = List.contains tag [ Some "ACCEPTED"; Some "ALREADY_ACCEPTED" ]


    let echoed =
        if receipt then
            let echoed =
                Frames.operationId invocation
                |> Option.map (fun id ->
                    update
                        (if endpoint = "operation.get" then
                             [ "outcome"; "data"; "receipt"; "operationId" ]
                         else
                             [ "outcome"; "data"; "operationId" ])
                        (JsonValue.String(id.ToString("D")))
                        echoed)
                |> Option.defaultValue echoed

            match
                memberText "kind" input,
                (at [ "target"; "caseReference" ] input
                 |> Option.bind (function
                     | JsonValue.String reference -> Some reference
                     | _ -> None))
            with
            | Some "CASE", Some reference ->
                update
                    [ "outcome"; "data"; "subject"; "caseReference" ]
                    (JsonValue.String reference)
                    echoed
            | _ -> echoed
        else
            echoed

    let echoed =
        if (endpoint = "case.get" || endpoint = "case.history") && tag = Some "SUCCEEDED" then
            match
                (at [ "target"; "caseReference" ] input
                 |> Option.bind (function
                     | JsonValue.String reference -> Some reference
                     | _ -> None))
            with
            | Some reference ->
                update [ "outcome"; "data"; "caseReference" ] (JsonValue.String reference) echoed
            | _ -> echoed
        elif endpoint = "authority.grants" && tag = Some "SUCCEEDED" then
            match JsonValue.tryMember "actorId" input with
            | Some actor -> update [ "outcome"; "data"; "actor"; "actor"; "actorId" ] actor echoed
            | _ -> echoed
        else
            echoed

    WireWriter.bytes echoed

let frameForReply endpoint bytes =
    let invocation = frame endpoint

    let root =
        StrictJson.read Limits.MaximumResponseBytes bytes
        |> Result.defaultWith (fun _ -> failtest "Positive mock JSON")

    let subject =
        at [ "outcome"; "data"; "subject" ] root
        |> Option.orElseWith (fun () -> at [ "outcome"; "data"; "review"; "subject" ] root)

    let family = subject |> Option.bind (memberText "kind")

    let act =
        subject
        |> Option.bind (fun subject ->
            memberText "act" subject
            |> Option.orElseWith (fun () ->
                match family, at [ "effects"; "kind" ] subject with
                | Some "WORK", _ -> Some "UPDATE_WORK"
                | Some "REMINDER", Some(JsonValue.String "CREATED") -> Some "CREATE"
                | Some "REMINDER", Some(JsonValue.String "COMPLETED_AND_PLANNED") ->
                    Some "COMPLETE_AND_PLAN_NEXT"
                | Some "REMINDER", Some(JsonValue.String "CHANGED") ->
                    match at [ "effects"; "reminder"; "state" ] subject with
                    | Some(JsonValue.String "COMPLETED") -> Some "COMPLETE"
                    | Some(JsonValue.String "CANCELLED") -> Some "CANCEL"
                    | _ -> Some "UPDATE"
                | _ -> None))

    // New families correlate the actual parent and independently revisioned entity. Choosing only
    // a family/act can pair a positive response with an unrelated authored request corpus entry.
    let familyContext value =
        match family, subject with
        | (Some "WORK" | Some "REMINDER"), Some subject ->
            let sameOperation =
                JsonValue.tryMember "operationId" value = JsonValue.tryMember "operationId" root

            let sameParent =
                match at [ "target"; "kind" ] value with
                | Some(JsonValue.String "ID") ->
                    at [ "target"; "caseId" ] value =
                        (JsonValue.tryMember "caseId" subject
                         |> Option.orElseWith (fun () -> at [ "before"; "caseId" ] subject))
                | Some(JsonValue.String "REFERENCE") ->
                    at [ "target"; "caseReference" ] value =
                        JsonValue.tryMember "caseReference" subject
                | _ -> false

            let sameRevision =
                if family = Some "WORK" then
                    let expected = JsonValue.tryMember "expectedRevision" value

                    match JsonValue.tryMember "revision" subject with
                    | Some(JsonValue.Integer revision) ->
                        expected = Some(JsonValue.Integer(revision - 1L))
                    | _ -> expected = at [ "before"; "revision" ] subject
                else
                    true

            sameOperation && sameParent && sameRevision
        | _ -> true

    if List.contains endpoint [ "operation.execute"; "operation.preview" ] then
        examples "examples/requests.json" endpoint
        |> List.tryFind (fun value ->
            memberText "kind" value = family
            && familyContext value
            && (act.IsNone || at [ "act"; "kind" ] value = (act |> Option.map JsonValue.String)))
        |> Option.map (fun input ->
            JsonValue.Object [ "endpoint", JsonValue.String endpoint; "input", input ]
            |> WireWriter.bytes
            |> Frames.admit
            |> Result.defaultWith (fun _ -> failtest "Independent authored family transport input"))
        |> Option.defaultValue invocation
    else
        invocation

let result invocation body =
    StrictJson.read Limits.MaximumResponseBytes body
    |> Result.bind (fun reply ->
        ServiceReply.read (frameEndpoint invocation) reply
        |> Result.map ServiceReply.json)

let replyJson (encoded: byte array) =
    let encoded = encoded |> Array.takeWhile ((<>) (byte '\n'))

    Frames.decode encoded
    |> Result.defaultWith (fun _ -> failwith "Invalid reply.")
    |> ignore

    use document = JsonDocument.Parse(ReadOnlyMemory<byte> encoded)
    document.RootElement.Clone()

let response status body =
    let message = new HttpResponseMessage(enum<HttpStatusCode> status)
    let content = new ByteArrayContent(body)
    content.Headers.ContentType <- MediaTypeHeaderValue "application/json"
    message.Content <- content
    message

type Handler(send: HttpRequestMessage -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, _) = send request

let send handler frame =
    use client = new HttpClient(handler)
    client.Timeout <- TimeSpan.FromSeconds 5.

    RemoteServiceCall.send
        client
        {
            InstallationId = Some(System.Guid.Parse "90000000-0000-7000-8000-000000000001")
            Service = (Uri "https://localhost/")
            Authorization = (AuthenticationHeaderValue("Bearer", "synthetic-token"))
        }
        frame
        ignore
        CancellationToken.None
    |> _.GetAwaiter().GetResult()

let call (input: byte array) invoke =
    use source = new MemoryStream(input)
    use output = new MemoryStream()
    use errors = new MemoryStream()

    use processor =
        RemoteFrameProcessor.ForTest(source, output, errors, invoke, (fun _ -> Task.FromResult()))

    let code = processor.Call CancellationToken.None
    code, output.ToArray(), errors.ToArray()
