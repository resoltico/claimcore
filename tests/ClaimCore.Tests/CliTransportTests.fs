module ClaimCore.Tests.CliTransportTests

open ClaimCore.ClientProtocol

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.ClientContract
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

    for endpoint in EndpointCatalog.all do
        for body in SchemaSamples.variants endpoint.Response do
            use document = JsonDocument.Parse(ReadOnlyMemory body)

            let tag =
                document.RootElement.GetProperty("outcome").GetProperty("tag").GetString()
                |> Option.ofObj
                |> Option.get

            let invocation = frame endpoint.Identifier

            let body =
                if
                    endpoint.Identifier = "authority.execute"
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
                ("Outcome selects exit for " + endpoint.Identifier + " " + tag)

            Expect.isOk (Frames.decode reply.Bytes) "Output is an admitted frame"
            seen <- Set.add tag seen

    for tag in Map.keys expected do
        Expect.isTrue (Set.contains tag seen) "Each specified outcome exit has a fixture"

let private requestBytes () =
    for endpoint in EndpointCatalog.all do
        let invocation = frame endpoint.Identifier

        use handler =
            new Handler(fun request ->
                task {
                    Expect.equal request.Method.Method endpoint.Method "Exact method"

                    Expect.equal
                        (request.RequestUri |> Option.ofObj |> Option.get |> _.AbsolutePath)
                        endpoint.Path
                        "Exact route"

                    Expect.equal
                        (request.Headers.GetValues("ClaimCore-Contract") |> Seq.toList)
                        [ WireContractFingerprint.value EndpointCatalog.fingerprint ]
                        "Compiled handshake"

                    if endpoint.Method = "POST" then
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
    for endpoint in EndpointCatalog.all do
        for reason in HostFailures.all do
            use handler =
                new Handler(fun _ ->
                    Task.FromResult(
                        response (HostFailures.status reason) (HostFailures.encode reason)
                    ))

            let expected =
                if endpoint.Mutates && HostFailures.phase reason <> Some "NOT_STARTED" then
                    4
                else
                    3

            Expect.equal
                (send handler (frame endpoint.Identifier)).ExitCode
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
            let expected = if (Frames.endpoint invocation).Mutates then 4 else 2

            Expect.equal
                (send handler invocation).ExitCode
                expected
                "Malformed or wrong-family reply cannot settle a mutation"

        use wrongStatus =
            new Handler(fun _ ->
                Task.FromResult(response 403 (HostFailures.encode HostFailure.SessionRejected)))

        Expect.equal
            (send wrongStatus (frame endpoint)).ExitCode
            (if (Frames.endpoint (frame endpoint)).Mutates then 4 else 2)
            "HTTP status is bound to failure body"

let private localExits () =
    for failure, expected in
        [
            ClientFailure.Configuration, 3
            ClientFailure.Authentication, 3
            ClientFailure.Unavailable, 3
            ClientFailure.Cancelled, 130
        ] do
        let code, output, _ =
            call (Frames.bytes (frame "case.get")) (fun _ _ _ ->
                Task.FromResult(CliReply.local failure))

        Expect.equal code expected "Local failure exit"
        Expect.isOk (Frames.decode output) "One stdout frame"

    let code, output, errors =
        call (Frames.bytes (frame "case.get")) (fun _ _ _ ->
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
        let mutates = (Frames.endpoint invocation).Mutates

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
        call (Frames.bytes (frame "authority.execute")) (fun _ dispatched _ ->
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

[<Tests>]
let tests =
    testList
        "CLI service transport"
        [
            testCase "response bounds media status and cancellation guards reject" (fun () ->
                responseBounds ()
                dispatchGuards ())
            testCase "all result tags select the specified exit" resultExits
            testCase "catalogue methods handshake and exact input bytes are sent" requestBytes
            testCase "all typed service failure phases preserve mutation knowledge" failurePhases
            testCase "invalid replies and forged status never settle a mutation" invalidReplies
            testCase
                "local cancellation unsupported invocation and pre-dispatch failure exits"
                localExits
        ]
