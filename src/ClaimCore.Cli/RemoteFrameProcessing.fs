namespace ClaimCore.Cli

open ClaimCore.Wire

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
            | Error _ -> Error ClientFailure.Configuration
            | Ok configuration ->
                match RemoteAccessSession.Open configuration with
                | Error _ -> Error ClientFailure.Configuration
                | Ok opened ->
                    session <- Some opened
                    Ok opened

    interface IDisposable with
        member _.Dispose() =
            session |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            session <- None

module private FrameInvocation =
    let invoke (holder: RemoteSessionHolder) frame beforeSend (cancelled: CancellationToken) =
        task {
            if cancelled.IsCancellationRequested then
                return CliReply.local ClientFailure.Cancelled
            else
                match holder.Acquire() with
                | Error failure -> return CliReply.local failure
                | Ok access ->
                    match! access.Token cancelled with
                    | Error _ when cancelled.IsCancellationRequested ->
                        return CliReply.local ClientFailure.Cancelled
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

type RemoteFrameProcessor
    private (input: Stream, output: Stream, errors: Stream, invoke, owned: IDisposable) =
    let delivery = ClaimCore.Cli.FrameDelivery(output, errors)

    let interpret frame stopped =
        match frame with
        | InputFrame.EndOfInput -> None
        | InputFrame.TooLarge -> Some(CliReply.protocol ProtocolFailure.InvalidFrame)
        | InputFrame.Bytes bytes ->
            match Frames.admit bytes with
            | Error _ -> Some(CliReply.protocol ProtocolFailure.InvalidFrame)
            | Ok frame ->
                delivery.Prepare frame

                Some(
                    invoke frame (fun () -> delivery.BeforeDispatch frame) stopped
                    |> fun (value: Task<CliReply>) -> value.GetAwaiter().GetResult()
                    |> fun reply ->
                        let decoded =
                            Frames.decode reply.Bytes
                            |> Result.defaultWith (fun _ -> invalidOp "Invalid local reply.")

                        let knowledge = (Frames.metadata decoded).AttemptKnowledge

                        CliReply.forAttempt frame knowledge reply
                )

    new(input, output, errors) =
        let holder = new RemoteSessionHolder()
        new RemoteFrameProcessor(input, output, errors, FrameInvocation.invoke holder, holder)

    static member internal ForTest(input, output, errors, invoke) =
        new RemoteFrameProcessor(
            input,
            output,
            errors,
            invoke,
            { new IDisposable with
                member _.Dispose() = ()
            }
        )

    member _.Interrupt() = delivery.Interrupt()

    member _.Call(stopped) =
        try
            delivery.BeginFrame()

            match interpret (FrameReader.readDocument input) stopped with
            | None -> 0
            | Some reply ->
                delivery.Write reply
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
