module ClaimCore.Tests.TransportBodyScenarios

open ClaimCore.Protocol

open ClaimCore.Domain
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
open ClaimCore.Tests.TransportFailureScenarios


let internal successfulBody endpoint invocation =
    let tag =
        if (frameEndpoint invocation |> Api.executesLedger) then
            "ACCEPTED"
        else
            "SUCCEEDED"

    samples endpoint
    |> List.find (fun body ->
        use document = JsonDocument.Parse(ReadOnlyMemory body)

        document.RootElement.GetProperty("outcome").GetProperty("kind").GetString() = tag
        && (result invocation body |> Result.isOk))

let internal mediaVariants () =
    for endpoint in [ "case.get"; "operation.execute" ] do
        let invocation = frame endpoint

        for status, body, exit in
            [
                200, successfulBody endpoint invocation, 0
                500, HostFailureCodecs.encode HostFailureReason.BeforeDispatchFailed, 3
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

let internal mixedMediaRefusals () =
    for endpoint in [ "case.get"; "operation.execute" ] do
        let invocation = frame endpoint

        let expected =
            if (frameEndpoint invocation |> Api.executesLedger) then
                4
            else
                2

        for media in [ Some "Application/JSON"; Some "text/plain"; None ] do
            for status, body in
                [
                    200, bytes "{"
                    200, bytes "{}"
                    403, HostFailureCodecs.encode HostFailureReason.SessionRejected
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

let internal absentJsonMedia () =
    for endpoint in [ "case.get"; "operation.execute" ] do
        let invocation = frame endpoint
        let body = successfulBody endpoint invocation

        let expected =
            if (frameEndpoint invocation |> Api.executesLedger) then
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

let internal mixedCaseJsonMedia () =
    let invocation = frame "operation.execute"
    let body = successfulBody "operation.execute" invocation

    use handler =
        new Handler(fun _ ->
            let message = response 200 body

            message.Content.Headers.ContentType <-
                System.Net.Http.Headers.MediaTypeHeaderValue "Application/JSON"

            Task.FromResult message)

    Expect.equal
        (send handler invocation).ExitCode
        0
        "JSON media type admission is case-insensitive"

let internal responseBounds () =
    for endpoint in [ "case.get"; "operation.execute" ] do
        let invocation = frame endpoint
        let executesLedger = (frameEndpoint invocation |> Api.executesLedger)

        use oversize =
            new Handler(fun _ ->
                Task.FromResult(
                    response
                        200
                        (Array.create (RemoteServiceCall.MaximumResponseBytes + 1) (byte ' '))
                ))

        Expect.equal
            (send oversize invocation).ExitCode
            (if executesLedger then 4 else 3)
            "Response buffering is bounded"

        use media =
            new Handler(fun _ ->
                let message =
                    response 500 (HostFailureCodecs.encode HostFailureReason.BeforeDispatchFailed)

                message.Content.Headers.ContentType <-
                    System.Net.Http.Headers.MediaTypeHeaderValue "text/html"

                Task.FromResult message)

        Expect.equal
            (send media invocation).ExitCode
            (if executesLedger then 4 else 2)
            "Wrong media type cannot settle a mutation"

let internal nearFrameLimitBody endpoint failure =
    let original =
        if failure then
            HostFailureCodecs.encode HostFailureReason.BeforeDispatchFailed
        else
            samples endpoint
            |> List.find (fun body ->
                use document = JsonDocument.Parse(ReadOnlyMemory<byte> body)

                document.RootElement.GetProperty("outcome").GetProperty("kind").GetString() =
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

let internal frameResponseBounds () =
    for endpoint in [ "case.get"; "operation.execute" ] do
        for failure in [ false; true ] do
            let body = nearFrameLimitBody endpoint failure
            let invocation = frame endpoint
            let selected = frameEndpoint invocation

            Expect.equal
                body.Length
                (Limits.MaximumResponseBytes - 32)
                "The HTTP body is within its own bound."

            if failure then
                Expect.isError
                    (HostFailureCodecs.decode body)
                    "Host failures reject injected prose."
            else
                Expect.isError
                    (result invocation body)
                    "Structured refusals reject an injected prose member."

            use handler =
                new Handler(fun _ -> Task.FromResult(response (if failure then 500 else 200) body))

            let reply = send handler invocation

            Expect.equal
                reply.ExitCode
                (if Api.executesLedger selected then 4 else 2)
                "An unencodable response cannot settle a mutation."

            Expect.isLessThanOrEqual
                reply.Bytes.Length
                Limits.MaximumResponseBytes
                "The delivered frame stays bounded."

            let frame = Frames.decode reply.Bytes |> Fixtures.accepted

            match frame.Result with
            | FrameResult.LocalFailure CliFailure.InvalidResponse -> ()
            | _ ->
                failtest "An invalid response must produce a bounded InvalidResponse local failure."


            Expect.equal
                frame.Delivery
                DeliveryState.Uncertain
                "The frame retains that dispatch occurred without an admitted reply."
