namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

open System
open System.IO
open System.Threading
open System.Threading.Tasks

type private RemoteSessionHolder(configuration: RemoteConfiguration option) =
    let mutable session: RemoteAccessSession option = None

    member _.Acquire() =
        match session with
        | Some value -> Ok value
        | None ->
            match configuration |> Option.map Ok |> Option.defaultWith RemoteConfiguration.load with
            | Error failure -> Error failure
            | Ok configuration ->
                match RemoteAccessSession.Open configuration with
                | Error failure -> Error failure
                | Ok opened ->
                    session <- Some opened
                    Ok opened

    member _.Discard(cancelled) =
        match session with
        | Some value -> value.DiscardTokens cancelled
        | None -> Task.FromResult()

    interface IDisposable with
        member _.Dispose() =
            session |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            session <- None

module private FrameInvocation =
    let invoke (holder: RemoteSessionHolder) frame beforeSend (cancelled: CancellationToken) =
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
                    | Error AccessFailure.OidcCredentialUnavailable ->
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
                                {
                                    Service = access.Configuration.Service
                                    Authorization = token.Authorization
                                }
                                frame
                                beforeSend
                                cancelled
        }

[<NoEquality; NoComparison>]
type internal RemoteStreams =
    {
        Input: Stream
        Output: Stream
        Errors: Stream
    }

[<NoEquality; NoComparison>]
type private RemoteTransport =
    {
        Invoke: Invocation -> (unit -> unit) -> CancellationToken -> Task<CliReply>
        Discard: CancellationToken -> Task<unit>
        Owned: IDisposable
    }

module private RemoteTransport =
    let create configuration =
        let holder = new RemoteSessionHolder(configuration)

        {
            Invoke = FrameInvocation.invoke holder
            Discard = holder.Discard
            Owned = holder
        }

type RemoteFrameProcessor
    private (streams: RemoteStreams, language, render, transport: RemoteTransport) =
    let input = streams.Input

    let delivery =
        ClaimCore.Cli.FrameDelivery(streams.Output, streams.Errors, language, render)

    let invoke = transport.Invoke

    let exchange frame stopped =
        delivery.Prepare frame

        task {
            let! reply = invoke frame (fun () -> delivery.BeforeDispatch frame) stopped
            return CliReply.forAttempt frame reply.Frame.Delivery reply
        }

    let interpret frame stopped =
        match frame with
        | InputFrame.EndOfInput -> None
        | InputFrame.TooLarge -> Some(CliReply.protocol CliFailure.InvalidFrame)
        | InputFrame.Bytes bytes ->
            match Frames.admit bytes with
            | Error _ -> Some(CliReply.protocol CliFailure.InvalidFrame)
            | Ok frame ->
                Some(exchange frame stopped |> fun value -> value.GetAwaiter().GetResult())

    new
        (
            input: Stream,
            output: Stream,
            errors: Stream,
            language: string,
            render: CliReply -> byte array
        ) =
        new RemoteFrameProcessor(
            {
                Input = input
                Output = output
                Errors = errors
            },
            language,
            render,
            RemoteTransport.create None
        )

    new(input: Stream, output: Stream, errors: Stream, language: string) =
        new RemoteFrameProcessor(input, output, errors, language, (fun reply -> reply.Bytes))

    static member internal ForShell(streams, configuration) =
        new RemoteFrameProcessor(
            streams,
            "en",
            (fun reply -> reply.Bytes),
            RemoteTransport.create (Some configuration)
        )

    static member internal ForTest(input, output, errors, invoke, discard) =
        new RemoteFrameProcessor(
            {
                Input = input
                Output = output
                Errors = errors
            },
            "en",
            (fun reply -> reply.Bytes),
            {
                Invoke = invoke
                Discard = discard
                Owned =
                    { new IDisposable with
                        member _.Dispose() = ()
                    }
            }
        )

    member internal _.BeginCommand(language) = delivery.BeginCommand(language)
    member internal _.DiscardAuthentication(cancelled) = transport.Discard cancelled

    /// Multi-step CLI flows share authentication and the same frozen-delivery state.
    member internal _.Exchange(frame, stopped) =
        delivery.BeginFrame()
        exchange frame stopped

    member internal _.Deliver(renderReply, reply) = delivery.WriteWith(renderReply, reply)
    member internal _.Failure(error) = delivery.Failure error

    member _.Interrupt() = delivery.Interrupt()

    member _.Call(stopped) =
        try
            delivery.BeginFrame()

            match interpret (FrameReader.readDocument input) stopped with
            | None -> ExitCode.value ExitCode.Success
            | Some reply ->
                delivery.Write reply

                reply.ExitCode
        with error ->
            delivery.Failure error

    member _.Session(stopped: CancellationToken) =
        try
            let mutable running = true
            let mutable exitCode = ExitCode.value ExitCode.Success

            while running && not stopped.IsCancellationRequested do
                delivery.BeginFrame()
                let incoming = FrameReader.readLine input

                match interpret incoming stopped with
                | None -> running <- false
                | Some reply ->
                    delivery.Write reply

                    exitCode <- reply.ExitCode

                    if
                        reply.ExitCode = ExitCode.value ExitCode.OutcomeUnknown
                        || reply.ExitCode = ExitCode.value ExitCode.Cancelled
                    then
                        running <- false

                if incoming = InputFrame.TooLarge then
                    running <- false

            if stopped.IsCancellationRequested then
                ExitCode.value ExitCode.Cancelled
            else
                exitCode
        with error ->
            delivery.Failure error

    interface IDisposable with
        member _.Dispose() = transport.Owned.Dispose()
