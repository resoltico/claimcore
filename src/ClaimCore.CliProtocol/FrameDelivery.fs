namespace ClaimCore.Cli

open System
open System.IO
open System.Text.Json
open ClaimCore.Contracts

/// One process-delivery state per frame. Only closed operation identity, never authored values,
/// can survive a failed stdout write. A completed frame clears all previous knowledge.
type FrameDelivery(output: Stream, errors: Stream) =
    let gate = obj ()
    let mutable interrupted = false
    let mutable phase = CliDeliveryPhase.Idle
    let mutable identity: (Guid * string option) option = None
    let mutable potentiallyChanged = false

    let mutates identifier =
        CliMutationCatalog.isMutation identifier

    let authoredIdentity (input: JsonElement) =
        let mutable operation = Unchecked.defaultof<JsonElement>
        let mutable digest = Unchecked.defaultof<JsonElement>

        if
            input.TryGetProperty("operationId", &operation)
            && operation.ValueKind = JsonValueKind.String
        then
            match Guid.TryParseExact(operation.GetString(), "D") with
            | true, value when value <> Guid.Empty ->
                let knownDigest =
                    if
                        input.TryGetProperty("requestSha256", &digest)
                        && digest.ValueKind = JsonValueKind.String
                    then
                        digest.GetString() |> Option.ofObj
                    else
                        None

                Some(value, knownDigest)
            | _ -> None
        else
            None

    let advance change =
        lock gate (fun () ->
            if interrupted then
                raise (OperationCanceledException())

            change ())

    member _.BeginFrame() =
        advance (fun () ->
            phase <- CliDeliveryPhase.Reading
            identity <- None
            potentiallyChanged <- false)

    member _.BeforeAcquire() =
        advance (fun () -> phase <- CliDeliveryPhase.AcquiringService)

    member _.BeforeRemoteDispatch(identifier: string, input: JsonElement) =
        advance (fun () ->
            phase <- CliDeliveryPhase.Dispatching
            identity <- authoredIdentity input
            potentiallyChanged <- mutates identifier)

    member _.ObserveRendered() =
        advance (fun () -> phase <- CliDeliveryPhase.ResultObserved)

    member _.Write(response: CliWireResponse) =
        advance (fun () -> phase <- CliDeliveryPhase.ResultAvailable response.ExitCode)
        output.Write(response.Bytes, 0, response.Bytes.Length)
        output.Flush()

        lock gate (fun () ->
            phase <- CliDeliveryPhase.Idle
            identity <- None
            potentiallyChanged <- false)

    /// Seals future dispatch before deciding the process signal exit code.
    member _.Interrupt() =
        lock gate (fun () ->
            interrupted <- true
            if potentiallyChanged then 4 else 130)

    member _.Failure(error: exn) =
        let phase, potentiallyChanged, identity =
            lock gate (fun () -> phase, potentiallyChanged, identity)

        let problem =
            match phase with
            | CliDeliveryPhase.Reading -> CliProcessProblem.InputReadFailed
            | CliDeliveryPhase.AcquiringService -> CliProcessProblem.ServiceAcquireFailed
            | CliDeliveryPhase.ResultObserved -> CliProcessProblem.SerializationFailed
            | CliDeliveryPhase.Dispatching -> CliProcessProblem.DispatchFailed
            | CliDeliveryPhase.ResultAvailable _ -> CliProcessProblem.OutputWriteFailed
            | CliDeliveryPhase.Idle -> CliProcessProblem.UnexpectedFailure

        error |> ignore

        try
            let bytes = CliProcessDiagnostics.encode problem phase potentiallyChanged identity
            errors.Write(bytes, 0, bytes.Length)
            errors.Flush()
        with _ ->
            ()

        CliProcessDiagnostics.exitCode problem potentiallyChanged
