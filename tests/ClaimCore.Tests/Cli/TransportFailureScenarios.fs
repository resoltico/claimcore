module ClaimCore.Tests.TransportFailureScenarios

open ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json


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


let internal resultExits () =
    let expected =
        Map.ofList
            [
                "ACCEPTED", 0
                "ALREADY_ACCEPTED", 0
                "REJECTED", 2
                "NOT_ACCEPTED", 2
                "FAILED", 3
                "OUTCOME_UNKNOWN", 4
                "UNRESOLVED", 4
            ]

    let mutable seen = Set.empty

    for endpoint in Api.all do
        for body in samples (Api.id endpoint) do
            use document = JsonDocument.Parse(ReadOnlyMemory body)

            let tag =
                document.RootElement.GetProperty("outcome").GetProperty("kind").GetString()
                |> Option.ofObj
                |> Option.get

            let invocation = frameForReply (Api.id endpoint) body
            let body = correlatedBody invocation body

            use handler = new Handler(fun _ -> Task.FromResult(response 200 body))
            let reply = send handler invocation

            Expect.equal
                reply.ExitCode
                (Map.tryFind tag expected |> Option.defaultValue 0)
                ("Outcome selects exit for " + (Api.id endpoint) + " " + tag)

            Expect.isOk (Frames.decode reply.Bytes) "Output is an admitted frame"
            seen <- Set.add tag seen

    for tag in Map.keys expected do
        Expect.isTrue (Set.contains tag seen) "Each specified outcome exit has a fixture"

let internal requestBytes () =
    for endpoint in Api.all do
        let invocation = frame (Api.id endpoint)

        use handler =
            new Handler(fun request ->
                task {
                    Expect.equal
                        request.Method.Method
                        ((Api.method endpoint).Method)
                        "Exact method"

                    Expect.equal
                        (request.RequestUri |> Option.ofObj |> Option.get |> _.AbsolutePath)
                        (("/api/" + Api.id endpoint))
                        "Exact route"

                    Expect.equal
                        (request.Headers.GetValues("ClaimCore-Contract") |> Seq.toList)
                        [ ContractIdentity.value ]
                        "Compiled handshake"

                    if ((Api.method endpoint).Method) = "POST" then
                        let content = request.Content |> Option.ofObj |> Option.get
                        let! sent = content.ReadAsByteArrayAsync()

                        Expect.equal
                            sent
                            (Frames.inputBytes invocation)
                            "Original admitted input bytes"
                    else
                        Expect.isNull request.Content "Session GET has no body"

                    return
                        response
                            500
                            (HostFailureCodecs.encode HostFailureReason.BeforeDispatchFailed)
                })

        Expect.equal
            (send handler invocation).ExitCode
            3
            "Admission refusal is definite for this attempt"

let internal failurePhases () =
    for endpoint in Api.all do
        for reason in ClaimCore.Protocol.HostFailureReason.all do
            use handler =
                new Handler(fun _ ->
                    Task.FromResult(
                        response
                            (ClaimCore.Protocol.HostFailureReason.status reason)
                            (HostFailureCodecs.encode reason)
                    ))

            let expected =
                if
                    ((Api.requiresInstallation endpoint)
                     && ClaimCore.Protocol.HostFailureReason.phase reason
                        <> Some ExecutionPhase.NotStarted)
                then
                    4
                else
                    3

            Expect.equal
                (send handler (frame (Api.id endpoint))).ExitCode
                expected
                "Phase and durable mutation determine uncertainty"

let internal invalidReplies () =
    for endpoint in [ "case.get"; "operation.execute"; "operation.execute" ] do
        for body in
            [
                bytes "{}"
                bytes "{"
                bytes "null"
                HostFailureCodecs.encode HostFailureReason.DispatchUnconfirmed
            ] do
            use handler = new Handler(fun _ -> Task.FromResult(response 200 body))
            let invocation = frame endpoint

            let expected =
                if (frameEndpoint invocation |> Api.executesLedger) then
                    4
                else
                    2

            Expect.equal
                (send handler invocation).ExitCode
                expected
                "Malformed or wrong-family reply cannot settle a mutation"

        use wrongStatus =
            new Handler(fun _ ->
                Task.FromResult(
                    response 403 (HostFailureCodecs.encode HostFailureReason.SessionRejected)
                ))

        Expect.equal
            (send wrongStatus (frame endpoint)).ExitCode
            (if (frame endpoint |> frameEndpoint |> Api.executesLedger) then
                 4
             else
                 2)
            "HTTP status is bound to failure body"

let internal localExits () =
    for failure, expected in
        [
            ClientFailure.Configuration, 3
            ClientFailure.Authentication, 3
            ClientFailure.Unavailable, 3
            ClientFailure.Cancelled, 130
            ClientFailure.Unexpected, 70
            ClientFailure.InvalidFrame, 2
            ClientFailure.InvalidResponse, 2
            ClientFailure.ContractMismatch, 2
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

let internal dispatchGuards () =
    let forged =
        HostFailureCodecs.encode HostFailureReason.SessionRejected
        |> Encoding.UTF8.GetString
        |> fun text -> text.Replace("NOT_STARTED", "STARTED_UNCONFIRMED") |> bytes

    Expect.isError
        (HostFailureCodecs.decode forged)
        "A phase cannot be detached from its diagnostic"

    use forgedHost = new Handler(fun _ -> Task.FromResult(response 401 forged))

    Expect.equal
        (send forgedHost (frame "operation.execute")).ExitCode
        4
        "A forged pre-dispatch guarantee cannot settle a mutation."


    let code, _, _ =
        call (frameBytes (frame "operation.execute")) (fun _ dispatched _ ->
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
            {
                InstallationId = Some(System.Guid.Parse "90000000-0000-4000-8000-000000000001")
                Service = (Uri "https://localhost/")
                Authorization =
                    (System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic-token"))
            }
            (frame "operation.execute")
            (fun () -> dispatched <- true)
            cancelled.Token
        |> _.GetAwaiter().GetResult()
        |> ignore

        failtest "Expected cancellation before dispatch"
    with :? OperationCanceledException ->
        Expect.isFalse dispatched "Cancellation is checked before dispatch knowledge"

let internal malformedBusinessInput () =
    let original = frame "operation.execute"
    let input = Frames.inputBytes original |> Encoding.UTF8.GetString

    let malformed =
        input.Replace("2026-01-01", "not-a-calendar-date").Replace("\"x\"", "\"\\u0078\"")

    let invocation =
        bytes ($"{{\"endpoint\":\"operation.execute\",\"input\":{malformed}}}")
        |> Frames.admit
        |> Fixtures.accepted

    let rejected =
        samples "operation.execute"
        |> List.find (fun body ->
            use document = JsonDocument.Parse(ReadOnlyMemory<byte> body)

            document.RootElement.GetProperty("outcome").GetProperty("kind").GetString() =
                "REJECTED")

    use handler =
        new Handler(fun request ->
            task {
                let! sent = (request.Content |> nonNull).ReadAsByteArrayAsync()

                Expect.equal
                    sent
                    (Frames.inputBytes invocation)
                    "Prepared malformed business content is frozen before dispatch."

                return response 200 rejected
            })

    let reply = send handler invocation
    Expect.equal reply.ExitCode 2 "A typed business rejection exits two after dispatch."

    let frame = Frames.decode reply.Bytes |> Fixtures.accepted

    match frame.Result with
    | FrameResult.Reply value ->
        Expect.equal
            (Frames.outcome (ServiceReply.json value))
            (Some "REJECTED")
            "Business refusal is an admitted result."
    | _ -> failtest "Expected a business refusal."

let internal preparationFailures () =
    let check status body =
        let mutable calls = 0

        use handler =
            new Handler(fun request ->
                calls <- calls + 1

                Expect.equal
                    (request.RequestUri |> nonNull).AbsolutePath
                    "/api/context"
                    "Only preparation is sent"

                Task.FromResult(response status body))

        use client = new HttpClient(handler)

        let result =
            RemoteServiceCall.context
                client
                {
                    Service = Uri "https://localhost/"
                    InstallationId = None
                    Authorization =
                        System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer",
                            "synthetic-token"
                        )
                }
                CancellationToken.None
            |> _.GetAwaiter().GetResult()

        Expect.equal calls 1 "One bounded preparation request"
        result

    let positiveBody =
        samples "context"
        |> List.find (fun body ->
            use document = JsonDocument.Parse(ReadOnlyMemory body)

            document.RootElement.GetProperty("outcome").GetProperty("kind").GetString() =
                "SUCCEEDED")

    let positive = check 200 positiveBody
    Expect.isOk positive "Matching context remains admitted"

    let mismatch =
        check 409 (HostFailureCodecs.encode HostFailureReason.ContractMismatch)

    match mismatch with
    | Error reply ->
        match reply.Frame.Result with
        | FrameResult.HostFailure failure ->
            Expect.equal
                failure.Code
                HostFailureCode.ContractMismatch
                "Keep the actual refusal cause"

            Expect.equal failure.Phase (Some ExecutionPhase.NotStarted) "Keep the host phase"
            Expect.equal reply.ExitCode 3 "A preparation host failure remains a host failure"
        | _ -> failtest "Preparation discarded the structured contract mismatch"
    | Ok _ -> failtest "A mismatched context cannot prepare a mutation"

    match check 409 (bytes "{}") with
    | Error reply ->
        match reply.Frame.Result with
        | FrameResult.LocalFailure CliFailure.InvalidResponse -> ()
        | _ -> failtest "An invalid host body must still refuse at transport admission"
    | Ok _ -> failtest "Malformed context cannot prepare a mutation"
