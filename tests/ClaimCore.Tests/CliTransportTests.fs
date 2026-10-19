module ClaimCore.Tests.CliTransportTests

open ClaimCore.Wire

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open CliTransportFixture

let private resultExits () =
    let expected =
        Map.ofList
            [
                "ACCEPTED", 0
                "ALREADY_ACCEPTED", 0
                "REJECTED", 2
                "FAILED", 3
                "OUTCOME_UNKNOWN", 4
                "CANCELLED", 130
            ]

    let mutable seen = Set.empty

    for endpoint in Endpoint.all do
        for body in samples "wire-response-examples.json" (Endpoint.id endpoint) do
            use document = JsonDocument.Parse(ReadOnlyMemory body)

            let tag =
                document.RootElement.GetProperty("outcome").GetProperty("tag").GetString()
                |> Option.ofObj
                |> Option.get

            let invocation = frame (Endpoint.id endpoint)

            let body =
                if
                    (Endpoint.id endpoint) = "authority.execute"
                    && (tag = "ACCEPTED" || tag = "ALREADY_ACCEPTED")
                then
                    let value = System.Text.Json.Nodes.JsonNode.Parse(body) |> nonNull

                    let receipt =
                        value["outcome"] |> nonNull |> fun outcome -> outcome["data"] |> nonNull

                    receipt["act"] <- System.Text.Json.Nodes.JsonValue.Create("REGISTER_ACTOR")
                    Encoding.UTF8.GetBytes(value.ToJsonString())
                else
                    body

            use handler = new Handler(fun _ -> Task.FromResult(response 200 body))
            let reply = send handler invocation

            Expect.equal
                reply.ExitCode
                (Map.tryFind tag expected |> Option.defaultValue 0)
                ("Outcome selects exit for " + (Endpoint.id endpoint) + " " + tag)

            Expect.isOk (Frames.decode reply.Bytes) "Output is an admitted frame"
            seen <- Set.add tag seen

    for tag in Map.keys expected do
        Expect.isTrue (Set.contains tag seen) "Each specified outcome exit has a fixture"

let private requestBytes () =
    for endpoint in Endpoint.all do
        let invocation = frame (Endpoint.id endpoint)

        use handler =
            new Handler(fun request ->
                task {
                    Expect.equal request.Method.Method (Endpoint.method endpoint) "Exact method"

                    Expect.equal
                        (request.RequestUri |> Option.ofObj |> Option.get |> _.AbsolutePath)
                        (Endpoint.path endpoint)
                        "Exact route"

                    Expect.equal
                        (request.Headers.GetValues("ClaimCore-Contract") |> Seq.toList)
                        [ Fingerprint.value ]
                        "Compiled handshake"

                    if (Endpoint.method endpoint) = "POST" then
                        let content = request.Content |> Option.ofObj |> Option.get
                        let! sent = content.ReadAsByteArrayAsync()

                        Expect.equal
                            sent
                            (Frames.inputBytes invocation)
                            "Original admitted input bytes"
                    else
                        Expect.isNull request.Content "Session GET has no body"

                    return response 500 (HostFailures.encode HostFailure.BeforeDispatchFailed)
                })

        Expect.equal (send handler invocation).ExitCode 3 "Definite admission refusal"

let private failurePhases () =
    for endpoint in Endpoint.all do
        for reason in HostFailures.all do
            use handler =
                new Handler(fun _ ->
                    Task.FromResult(
                        response (HostFailures.status reason) (HostFailures.encode reason)
                    ))

            let expected =
                if
                    (Endpoint.mutates endpoint)
                    && HostFailures.phase reason <> Some ExecutionPhase.NotStarted
                then
                    4
                else
                    3

            Expect.equal
                (send handler (frame (Endpoint.id endpoint))).ExitCode
                expected
                "Phase and durable mutation determine uncertainty"

let private invalidReplies () =
    for endpoint in [ "case.get"; "case.execute"; "authority.execute" ] do
        for body in
            [
                bytes "{}"
                bytes "{"
                bytes "null"
                HostFailures.encode HostFailure.DispatchUnconfirmed
            ] do
            use handler = new Handler(fun _ -> Task.FromResult(response 200 body))
            let invocation = frame endpoint

            let expected =
                if (frameEndpoint invocation |> Endpoint.mutates) then
                    4
                else
                    2

            Expect.equal
                (send handler invocation).ExitCode
                expected
                "Malformed or wrong-family reply cannot settle a mutation"

        use wrongStatus =
            new Handler(fun _ ->
                Task.FromResult(response 403 (HostFailures.encode HostFailure.SessionRejected)))

        Expect.equal
            (send wrongStatus (frame endpoint)).ExitCode
            (if (frame endpoint |> frameEndpoint |> Endpoint.mutates) then
                 4
             else
                 2)
            "HTTP status is bound to failure body"

let private successfulBody endpoint invocation =
    let tag =
        if (frameEndpoint invocation |> Endpoint.mutates) then
            "ACCEPTED"
        else
            "SUCCEEDED"

    samples "wire-response-examples.json" endpoint
    |> List.find (fun body ->
        use document = JsonDocument.Parse(ReadOnlyMemory body)

        document.RootElement.GetProperty("outcome").GetProperty("tag").GetString() = tag
        && (result invocation body |> Result.isOk))

let private mediaVariants () =
    for endpoint in [ "case.get"; "case.execute" ] do
        let invocation = frame endpoint

        for status, body, exit in
            [
                200, successfulBody endpoint invocation, 0
                500, HostFailures.encode HostFailure.BeforeDispatchFailed, 3
            ] do
            use canonical = new Handler(fun _ -> Task.FromResult(response status body))
            let expected = send canonical invocation
            Expect.equal expected.ExitCode exit "Canonical result and delivery exit"

            for media in
                [ "application/json"; "APPLICATION/JSON"; "Application/Json; charset=utf-8" ] do
                use handler =
                    new Handler(fun _ ->
                        let message = response status body

                        message.Content.Headers.ContentType <-
                            System.Net.Http.Headers.MediaTypeHeaderValue.Parse media

                        Task.FromResult message)

                let actual = send handler invocation
                Expect.equal actual.ExitCode expected.ExitCode "Media casing retains exit"

                Expect.equal
                    actual.Bytes
                    expected.Bytes
                    "Typed result and delivery remain identical"

let private mixedMediaRefusals () =
    for endpoint in [ "case.get"; "case.execute" ] do
        let invocation = frame endpoint

        let expected =
            if (frameEndpoint invocation |> Endpoint.mutates) then
                4
            else
                2

        for media in [ Some "Application/JSON"; Some "text/plain"; None ] do
            for status, body in
                [
                    200, bytes "{"
                    200, bytes "{}"
                    403, HostFailures.encode HostFailure.SessionRejected
                ] do
                use handler =
                    new Handler(fun _ ->
                        let message = response status body

                        message.Content.Headers.ContentType <-
                            media
                            |> Option.map System.Net.Http.Headers.MediaTypeHeaderValue.Parse
                            |> Option.toObj

                        Task.FromResult message)

                Expect.equal
                    (send handler invocation).ExitCode
                    expected
                    "Invalid replies stay refused"

let private absentJsonMedia () =
    for endpoint in [ "case.get"; "case.execute" ] do
        let invocation = frame endpoint
        let body = successfulBody endpoint invocation

        let expected =
            if (frameEndpoint invocation |> Endpoint.mutates) then
                4
            else
                2

        for media in [ Some "text/plain"; None ] do
            use handler =
                new Handler(fun _ ->
                    let message = response 200 body

                    message.Content.Headers.ContentType <-
                        media
                        |> Option.map System.Net.Http.Headers.MediaTypeHeaderValue.Parse
                        |> Option.toObj

                    Task.FromResult message)

            Expect.equal (send handler invocation).ExitCode expected "JSON media is required"

let private mixedMediaCorrelation () =
    let invocation = frame "case.execute"

    let node =
        successfulBody "case.execute" invocation
        |> System.Text.Json.Nodes.JsonNode.Parse
        |> nonNull

    let receipt = node["outcome"] |> nonNull |> fun value -> value["data"] |> nonNull
    receipt["caseReference"] <- System.Text.Json.Nodes.JsonValue.Create("another")
    let body = node.ToJsonString() |> bytes

    Expect.isError (result invocation body) "Changed receipt identity is uncorrelated"

    use handler =
        new Handler(fun _ ->
            let message = response 200 body

            message.Content.Headers.ContentType <-
                System.Net.Http.Headers.MediaTypeHeaderValue "Application/JSON"

            Task.FromResult message)

    Expect.equal (send handler invocation).ExitCode 4 "Mixed media cannot settle wrong correlation"

let private localExits () =
    for failure, expected in
        [
            ClientFailure.Configuration, 3
            ClientFailure.Authentication, 3
            ClientFailure.Unavailable, 3
            ClientFailure.Cancelled, 130
        ] do
        let code, output, _ =
            call (frameBytes (frame "case.get")) (fun _ _ _ ->
                Task.FromResult(CliReply.local failure))

        Expect.equal code expected "Local failure exit"
        Expect.isOk (Frames.decode output) "One stdout frame"

    let code, output, errors =
        call (frameBytes (frame "case.get")) (fun _ _ _ ->
            raise (InvalidOperationException "synthetic-secret"))

    Expect.equal code 70 "Unexpected failure before dispatch"
    Expect.isEmpty output "No success output"

    Expect.isFalse
        ((Encoding.UTF8.GetString errors).Contains "synthetic-secret")
        "Exception message is not delivered"

    let start =
        System.Diagnostics.ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    start.ArgumentList.Add(typeof<RemoteConfiguration>.Assembly.Location)
    start.ArgumentList.Add("unsupported.synthetic")
    use child = System.Diagnostics.Process.Start(start) |> Option.ofObj |> Option.get

    if not (child.WaitForExit(10000)) then
        child.Kill(true)
        failtest "CLI process did not exit."

    Expect.equal child.ExitCode 64 "Unsupported invocation"
    Expect.equal (child.StandardOutput.ReadToEnd()) "" "Unsupported invocation has no result"

let private responseBounds () =
    for endpoint in [ "case.get"; "authority.execute" ] do
        let invocation = frame endpoint
        let mutates = (frameEndpoint invocation |> Endpoint.mutates)

        use oversize =
            new Handler(fun _ ->
                Task.FromResult(
                    response
                        200
                        (Array.create (RemoteServiceCall.MaximumResponseBytes + 1) (byte ' '))
                ))

        Expect.equal
            (send oversize invocation).ExitCode
            (if mutates then 4 else 3)
            "Response buffering is bounded"

        use media =
            new Handler(fun _ ->
                let message = response 500 (HostFailures.encode HostFailure.BeforeDispatchFailed)

                message.Content.Headers.ContentType <-
                    System.Net.Http.Headers.MediaTypeHeaderValue "text/html"

                Task.FromResult message)

        Expect.equal
            (send media invocation).ExitCode
            (if mutates then 4 else 2)
            "Wrong media type cannot settle a mutation"

let private dispatchGuards () =
    let forged =
        HostFailures.encode HostFailure.DispatchUnconfirmed
        |> Encoding.UTF8.GetString
        |> fun text -> text.Replace("STARTED_UNCONFIRMED", "NOT_STARTED") |> bytes

    Expect.isError (HostFailures.decode forged) "A phase cannot be detached from its diagnostic"

    let code, _, _ =
        call (frameBytes (frame "authority.execute")) (fun _ dispatched _ ->
            dispatched ()
            raise (InvalidOperationException "synthetic-after-dispatch"))

    Expect.equal code 4 "Unexpected failure after dispatch is uncertain"
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()
    use handler = new Handler(fun _ -> failtest "Cancelled input must not send")
    use client = new HttpClient(handler)
    let mutable dispatched = false

    try
        RemoteServiceCall.send
            client
            (Uri "https://localhost/")
            (frame "authority.execute")
            (System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic-token"))
            (fun () -> dispatched <- true)
            cancelled.Token
        |> _.GetAwaiter().GetResult()
        |> ignore

        failtest "Expected cancellation before dispatch"
    with :? OperationCanceledException ->
        Expect.isFalse dispatched "Cancellation is checked before dispatch knowledge"

let private malformedBusinessInput () =
    let original = frame "case.execute"
    let input = Frames.inputBytes original |> Encoding.UTF8.GetString

    let malformed =
        input.Replace("2026-01-01", "not-a-calendar-date").Replace("\"x\"", "\"\\u0078\"")

    let invocation =
        bytes ($"{{\"endpoint\":\"case.execute\",\"input\":{malformed}}}")
        |> Frames.admit
        |> Fixtures.accepted

    let rejected =
        samples "wire-response-examples.json" "case.execute"
        |> List.find (fun body ->
            use document = JsonDocument.Parse(ReadOnlyMemory<byte> body)
            document.RootElement.GetProperty("outcome").GetProperty("tag").GetString() = "REJECTED")

    use handler =
        new Handler(fun request ->
            task {
                let! sent = (request.Content |> nonNull).ReadAsByteArrayAsync()

                Expect.equal
                    sent
                    (bytes malformed)
                    "The malformed business string and original escape spelling are sent unchanged."

                return response 200 rejected
            })

    let reply = send handler invocation
    Expect.equal reply.ExitCode 2 "A typed business rejection exits two after dispatch."

    match Frames.decode reply.Bytes |> Fixtures.accepted with
    | FrameReply.CaseExecute(_, CaseExecuteOutcome.Rejected _) -> ()
    | _ -> failtest "Business format refusal remains a typed result, not a protocol failure."

let private nearFrameLimitBody endpoint failure =
    let original =
        if failure then
            HostFailures.encode HostFailure.BeforeDispatchFailed
        else
            samples "wire-response-examples.json" endpoint
            |> List.find (fun body ->
                use document = JsonDocument.Parse(ReadOnlyMemory<byte> body)

                document.RootElement.GetProperty("outcome").GetProperty("tag").GetString() =
                    "REJECTED")

    let node = System.Text.Json.Nodes.JsonNode.Parse(original) |> nonNull

    let target =
        if failure then
            node
        else
            node["outcome"] |> nonNull |> fun outcome -> outcome["data"] |> nonNull

    target["message"] <- System.Text.Json.Nodes.JsonValue.Create("x")
    let baseline = node.ToJsonString() |> bytes

    target["message"] <-
        System.Text.Json.Nodes.JsonValue.Create(
            String('a', Limits.MaximumResponseBytes - baseline.Length - 31)
        )

    node.ToJsonString() |> bytes

let private frameResponseBounds () =
    for endpoint in [ "case.get"; "case.execute" ] do
        for failure in [ false; true ] do
            let body = nearFrameLimitBody endpoint failure
            let invocation = frame endpoint
            let selected = frameEndpoint invocation

            Expect.equal
                body.Length
                (Limits.MaximumResponseBytes - 32)
                "The HTTP body is within its own bound."

            if failure then
                Expect.isOk (HostFailures.decode body) "Host failure body is well formed."
            else
                Expect.isOk (Replies.decode selected body) "Outcome body is well formed."

            use handler =
                new Handler(fun _ -> Task.FromResult(response (if failure then 500 else 200) body))

            let reply = send handler invocation

            Expect.equal
                reply.ExitCode
                (if Endpoint.mutates selected then 4 else 2)
                "An unencodable response cannot settle a mutation."

            Expect.isLessThanOrEqual
                reply.Bytes.Length
                Limits.MaximumResponseBytes
                "The delivered frame stays bounded."

            match Frames.decode reply.Bytes |> Fixtures.accepted with
            | FrameReply.ProtocolFailure(meta, _, ProtocolFailure.InvalidResponse) ->
                Expect.equal
                    meta.AttemptKnowledge
                    (if Endpoint.mutates selected then
                         AttemptKnowledge.Unconfirmed
                     else
                         AttemptKnowledge.ReadFailed)
                    "Invalid response metadata preserves dispatch uncertainty."
            | _ -> failtest "Expected a bounded invalid-response frame."

[<Tests>]
let tests =
    testList
        "CLI service transport"
        [
            testCase "response bounds media status and cancellation guards reject" (fun () ->
                responseBounds ()
                dispatchGuards ()
                mediaVariants ()
                mixedMediaRefusals ()
                absentJsonMedia ()
                mixedMediaCorrelation ())
            testCase
                "malformed business strings reach typed server rejection unchanged"
                malformedBusinessInput
            testCase
                "HTTP bodies near the byte cap cannot create oversized reply frames"
                frameResponseBounds
            testCase "all result tags select the specified exit" resultExits
            testCase "catalogue methods handshake and exact input bytes are sent" requestBytes
            testCase "all typed service failure phases preserve mutation knowledge" failurePhases
            testCase "invalid replies and forged status never settle a mutation" invalidReplies
            testCase
                "local cancellation unsupported invocation and pre-dispatch failure exits"
                localExits
        ]
