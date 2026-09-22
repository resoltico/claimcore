namespace ClaimCore.Cli

open System
open System.IO
open System.Threading
open ClaimCore.Contracts

/// One state machine per handleFrame, explicitly reset before every frame. No prior frame's identity
/// can survive a successful flush or become evidence for a later input error.
type FrameDelivery(output: Stream, errors: Stream) =
    let mutable phase = CliDeliveryPhase.Idle
    let mutable identity: (Guid * string option) option = None
    let mutable potentiallyChanged = false

    let invocationIdentity endpoint input =
        match endpoint, input with
        | Endpoint.CommandPrepare, EndpointInput.Draft draft
        | Endpoint.CommandExecute, EndpointInput.Draft draft -> Some(draft.OperationId, None)
        | Endpoint.RecoveryResolve, EndpointInput.RecoveryResolve(operationId, digest)
        | Endpoint.RecoveryDismiss, EndpointInput.RecoveryDismiss(operationId, digest) ->
            Some(operationId, Some digest)
        | _ -> None

    let mutates =
        function
        | Endpoint.CommandPrepare
        | Endpoint.CommandExecute
        | Endpoint.RecoveryResolve
        | Endpoint.RecoveryDismiss
        | Endpoint.RecoveryImportEnvelopeRetain
        | Endpoint.RecoveryImportRecordRetain -> true
        | _ -> false

    member _.BeginFrame() =
        phase <- CliDeliveryPhase.Reading
        identity <- None
        potentiallyChanged <- false

    member _.BeforeAcquire() =
        phase <- CliDeliveryPhase.AcquiringRuntime

    member _.Observe(reply: EndpointReply) =
        phase <- CliDeliveryPhase.ResultObserved
        identity <- ReplyRecoveryContext.combine identity reply

    member _.BeforeDispatch(endpoint, input) =
        phase <- CliDeliveryPhase.Dispatching
        identity <- invocationIdentity endpoint input
        potentiallyChanged <- mutates endpoint

    member _.Write(response: RenderedResponse) =
        phase <- CliDeliveryPhase.ResultAvailable response.ExitCode
        output.Write(response.Bytes, 0, response.Bytes.Length)
        output.Flush()
        // Delivery success is not a peer acknowledgement. It merely ends this frame's host state.
        phase <- CliDeliveryPhase.Idle
        identity <- None
        potentiallyChanged <- false

    member _.Failure(error: exn) =
        let problem =
            match phase with
            | CliDeliveryPhase.Reading -> CliProcessProblem.InputReadFailed
            | CliDeliveryPhase.AcquiringRuntime -> CliProcessProblem.RuntimeAcquireFailed
            | CliDeliveryPhase.ResultObserved -> CliProcessProblem.SerializationFailed
            | CliDeliveryPhase.Dispatching -> CliProcessProblem.DispatchFailed
            | CliDeliveryPhase.ResultAvailable _ -> CliProcessProblem.OutputWriteFailed
            | CliDeliveryPhase.Idle -> CliProcessProblem.UnexpectedFailure
        // The exception is intentionally not serialized, named or inspected for message content.
        error |> ignore

        try
            let bytes = CliProcessDiagnostics.encode problem phase potentiallyChanged identity
            errors.Write(bytes, 0, bytes.Length)
            errors.Flush()
        with _ ->
            ()

        CliProcessDiagnostics.exitCode problem potentiallyChanged

module FrameProcessing =
    let private invoke (delivery: FrameDelivery) (session: InvocationSession) stopped bytes =
        match StrictJson.parseDocument 131072 bytes with
        | Error problem -> EndpointReply.Protocol(2, problem)
        | Ok document ->
            use source = document

            match InvocationFraming.decode source.RootElement with
            | Error problem -> EndpointReply.Protocol(2, problem)
            | Ok(endpoint, input, timeout) ->
                delivery.BeforeAcquire()

                session
                    .Run(
                        endpoint,
                        input,
                        timeout,
                        stopped,
                        fun () -> delivery.BeforeDispatch(endpoint, input)
                    )
                    .GetAwaiter()
                    .GetResult()

    let private handleFrame delivery session stopped frame =
        match frame with
        | InputFrame.Bytes bytes -> Some(invoke delivery session stopped bytes)
        | InputFrame.Failure problem -> Some(EndpointReply.Protocol(2, problem))
        | InputFrame.EndOfInput -> None

    let call supplier (input: Stream) output errors stopped =
        let delivery = FrameDelivery(output, errors)
        let session = InvocationSession(supplier)

        try
            delivery.BeginFrame()

            match FrameReader.readDocument 131072 input |> handleFrame delivery session stopped with
            | None -> 0
            | Some response ->
                delivery.Observe response
                let encoded = EndpointReply.encode response
                delivery.Write encoded
                encoded.ExitCode
        with error ->
            delivery.Failure error

    let session supplier (input: Stream) output errors (stopped: CancellationToken) =
        let delivery = FrameDelivery(output, errors)
        let session = InvocationSession(supplier)

        try
            let mutable running = true

            while running && not stopped.IsCancellationRequested do
                delivery.BeginFrame()

                match FrameReader.readLine 131072 input |> handleFrame delivery session stopped with
                | None -> running <- false
                | Some response ->
                    delivery.Observe response
                    delivery.Write(EndpointReply.encode response)

            if stopped.IsCancellationRequested then 130 else 0
        with error ->
            delivery.Failure error
