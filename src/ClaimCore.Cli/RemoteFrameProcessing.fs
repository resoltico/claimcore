namespace ClaimCore.Cli

open ClaimCore.Contract


open System
open System.IO
open System.Threading
open System.Threading.Tasks

type private RemoteSessionHolder() =
    let mutable session: RemoteAccessSession option = None

    member _.Acquire() =
        match session with
        | Some value -> Ok value
        | None ->
            match RemoteConfiguration.fromEnvironment () with
            | Error failure -> Error failure
            | Ok configuration ->
                match RemoteAccessSession.Open configuration with
                | Error failure -> Error failure
                | Ok opened ->
                    session <- Some opened
                    Ok opened

    interface IDisposable with
        member _.Dispose() =
            session |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            session <- None

module internal RecoveryAcknowledgement =
    let frame (reply: CliReply) =
        match Frames.decode reply.Bytes with
        | Ok {
                 Delivery = DeliveryState.Delivered
                 Result = FrameResult.Reply body
                 Endpoint = Some endpoint
             } when endpoint = Api.operationExecute.Id || endpoint = Api.operationRetry.Id ->
            match Api.operationExecute.Output.Read "$" body with
            | Ok {
                     RecoveryId = Some id
                     Outcome = (ExecuteResult.Accepted _ | ExecuteResult.AlreadyAccepted _ | ExecuteResult.Rejected _)
                 } ->
                JsonValue.Object
                    [
                        "endpoint", JsonValue.String Api.operationForget.Id
                        "input", Api.operationForget.Input.Write { RecoveryId = id }
                    ]
                |> WireWriter.bytes
                |> Frames.admit
                |> Result.toOption
            | _ -> None
        | _ -> None

module private FrameInvocation =
    let private send (holder: RemoteSessionHolder) frame beforeSend (cancelled: CancellationToken) =
        task {
            if cancelled.IsCancellationRequested then
                return CliReply.local ClientFailure.Cancelled
            elif not StandardSinks.supportsRemote then
                return CliReply.local ClientFailure.Configuration
            else
                match holder.Acquire() with
                | Error failure -> return CliReply.local failure
                | Ok access ->
                    match! access.Token cancelled with
                    | Error _ when cancelled.IsCancellationRequested ->
                        return CliReply.local ClientFailure.Cancelled
                    | Error "OIDC_CREDENTIAL_UNAVAILABLE" ->
                        return
                            CliReply.local (
                                ClientFailure.ConfigurationIssue(
                                    CliSetting.SecretFile,
                                    ConfigurationIssue.PrivateInputRefused
                                )
                            )
                    | Error _ -> return CliReply.local ClientFailure.Authentication
                    | Ok token ->
                        return!
                            RemoteServiceCall.send
                                access.Client
                                access.Configuration.Service
                                frame
                                token.Authorization
                                beforeSend
                                cancelled
        }

    let invoke (holder: RemoteSessionHolder) frame beforeSend (cancelled: CancellationToken) =
        task {
            if Api.id (Frames.endpoint frame) <> Api.operationRetry.Id then
                return! send holder frame beforeSend cancelled
            else
                match holder.Acquire() with
                | Error failure -> return CliReply.local failure
                | Ok access ->
                    use deadline = HttpRequestDeadline.link access.Client cancelled
                    return! RecoverySelection.invoke (send holder) frame beforeSend deadline.Token
        }

    let acknowledge holder frame (stopped: CancellationToken) =
        task {
            use bound = CancellationTokenSource.CreateLinkedTokenSource(stopped)
            bound.CancelAfter(TimeSpan.FromSeconds 5.)
            let! _ = invoke holder frame ignore bound.Token
            return ()
        }

type RemoteFrameProcessor
    private
    (
        input: Stream,
        output: Stream,
        errors: Stream,
        invoke,
        owned: IDisposable,
        retry,
        acknowledge: Invocation -> CancellationToken -> Task<unit>
    ) =
    let delivery = ClaimCore.Cli.FrameDelivery(output, errors)

    let acknowledgeReply reply stopped =
        match RecoveryAcknowledgement.frame reply with
        | None -> ()
        | Some frame ->
            try
                acknowledge frame stopped |> fun pending -> pending.GetAwaiter().GetResult()
            with _ ->
                ()

    let interpret frame stopped =
        match frame with
        | InputFrame.EndOfInput -> None
        | InputFrame.TooLarge -> Some(CliReply.protocol CliFailure.InvalidFrame)
        | InputFrame.Bytes bytes ->
            match Frames.admit bytes with
            | Error _ -> Some(CliReply.protocol CliFailure.InvalidFrame)
            | Ok frame when retry && not (Api.executesLedger (Frames.endpoint frame)) ->
                Some(CliReply.protocol CliFailure.InvalidFrame)
            | Ok admitted ->
                let frame = if retry then Frames.retry admitted else admitted
                delivery.Prepare frame

                Some(
                    invoke frame (fun () -> delivery.BeforeDispatch frame) stopped
                    |> fun (value: Task<CliReply>) -> value.GetAwaiter().GetResult()
                    |> fun reply ->
                        let decoded =
                            Frames.decode reply.Bytes
                            |> Result.defaultWith (fun _ -> invalidOp "Invalid local reply.")

                        let delivery = decoded.Delivery

                        CliReply.forAttempt frame delivery reply
                )

    new(input, output, errors, retry) =
        let holder = new RemoteSessionHolder()

        new RemoteFrameProcessor(
            input,
            output,
            errors,
            FrameInvocation.invoke holder,
            holder,
            retry,
            FrameInvocation.acknowledge holder
        )

    new(input, output, errors) = new RemoteFrameProcessor(input, output, errors, false)

    static member internal ForTest(input, output, errors, invoke, ?retry, ?acknowledge) =
        new RemoteFrameProcessor(
            input,
            output,
            errors,
            invoke,
            { new IDisposable with
                member _.Dispose() = ()
            },
            defaultArg retry false,
            defaultArg acknowledge (fun _ _ -> Task.FromResult())
        )

    member _.Interrupt() = delivery.Interrupt()

    member _.Call(stopped) =
        try
            delivery.BeginFrame()

            match interpret (FrameReader.readDocument input) stopped with
            | None -> 0
            | Some reply ->
                delivery.Write reply

                acknowledgeReply reply stopped

                reply.ExitCode
        with error ->
            delivery.Failure error

    member _.Session(stopped: CancellationToken) =
        try
            let mutable running = true
            let mutable exitCode = 0

            while running && not stopped.IsCancellationRequested do
                delivery.BeginFrame()
                let incoming = FrameReader.readLine input

                match interpret incoming stopped with
                | None -> running <- false
                | Some reply ->
                    delivery.Write reply

                    acknowledgeReply reply stopped

                    exitCode <- reply.ExitCode

                    if reply.ExitCode = 4 || reply.ExitCode = 130 then
                        running <- false

                if incoming = InputFrame.TooLarge then
                    running <- false

            if stopped.IsCancellationRequested then 130 else exitCode
        with error ->
            delivery.Failure error

    interface IDisposable with
        member _.Dispose() = owned.Dispose()
