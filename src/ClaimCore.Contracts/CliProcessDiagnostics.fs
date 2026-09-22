namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text.Json

[<RequireQualifiedAccess>]
type CliProcessProblem =
    | UnsupportedInvocation
    | InputReadFailed
    | DispatchFailed
    | RuntimeAcquireFailed
    | SerializationFailed
    | OutputWriteFailed
    | UnexpectedFailure

[<RequireQualifiedAccess>]
type CliDeliveryPhase =
    | Idle
    | Reading
    | AcquiringRuntime
    | ResultObserved
    | Dispatching
    | ResultAvailable of exitCode: int

/// Process delivery is not a core outcome. IDs are separate recovery context, never message args.
module CliProcessDiagnostics =
    let private policies =
        [
            CliProcessProblem.UnsupportedInvocation,
            ("CLI_INVOCATION_UNSUPPORTED",
             "Run claimcore help for the supported invocation grammar.")
            CliProcessProblem.InputReadFailed,
            ("CLI_INPUT_READ_FAILED",
             "Input delivery failed. No work from this frame was dispatched.")
            CliProcessProblem.DispatchFailed,
            ("CLI_DISPATCH_UNCONFIRMED",
             "The dispatched call did not produce a confirmed response. Do not infer rollback or retry with a new identity.")
            CliProcessProblem.OutputWriteFailed,
            ("CLI_OUTPUT_DELIVERY_FAILED",
             "The current response could not be delivered completely. Reconcile potentially state-changing work; do not infer rollback.")
            CliProcessProblem.UnexpectedFailure,
            ("CLI_PROCESS_FAILED",
             "The local process failed. Inspect its configuration and retained operation state before continuing.")
        ]

    let private additional =
        [
            CliProcessProblem.RuntimeAcquireFailed,
            ("CLI_RUNTIME_ACQUIRE_FAILED",
             "The runtime supplier failed before this frame was dispatched.")
            CliProcessProblem.SerializationFailed,
            ("CLI_RESULT_ENCODING_FAILED",
             "A typed result was observed but could not be encoded. Preserve the exact operation identity; no retry has been attempted.")
        ]

    let all = (policies @ additional) |> List.map fst

    let token reason =
        (policies @ additional) |> List.find (fst >> (=) reason) |> snd |> fst

    let private phaseToken =
        function
        | CliDeliveryPhase.Idle -> "IDLE"
        | CliDeliveryPhase.Reading -> "READING"
        | CliDeliveryPhase.AcquiringRuntime -> "ACQUIRING_RUNTIME"
        | CliDeliveryPhase.ResultObserved -> "RESULT_OBSERVED"
        | CliDeliveryPhase.Dispatching -> "DISPATCH_UNCONFIRMED"
        | CliDeliveryPhase.ResultAvailable _ -> "RESULT_AVAILABLE"

    let exitCode reason potentiallyChanged =
        if reason = CliProcessProblem.UnsupportedInvocation then 64
        elif potentiallyChanged then 4
        elif reason = CliProcessProblem.UnexpectedFailure then 70
        else 3

    let encode reason phase potentiallyChanged (identity: (Guid * string option) option) =
        let id, message = (policies @ additional) |> List.find (fst >> (=) reason) |> snd
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("kind", "cliProcessFailure")
        writer.WritePropertyName("diagnostic")
        writer.WriteStartObject()
        writer.WriteString("id", id)
        writer.WritePropertyName("parameters")
        writer.WriteStartObject()
        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.WriteString("message", message)
        writer.WriteString("deliveryPhase", phaseToken phase)
        writer.WriteBoolean("potentiallyChanged", potentiallyChanged)

        match phase with
        | CliDeliveryPhase.ResultAvailable code -> writer.WriteNumber("resultExitCode", code)
        | _ -> writer.WriteNull("resultExitCode")

        match identity with
        | Some(operationId, digest) ->
            writer.WriteString("operationId", operationId.ToString("D"))

            match digest with
            | Some value -> writer.WriteString("requestSha256", value)
            | None -> writer.WriteNull("requestSha256")
        | None ->
            writer.WriteNull("operationId")
            writer.WriteNull("requestSha256")

        writer.WriteString("recommendedAction", "STOP_AND_INVESTIGATE")
        writer.WriteEndObject()
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
