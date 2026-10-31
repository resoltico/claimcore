namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
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
    let private text name value =
        JsonValue.tryMember name value
        |> Option.bind (function
            | JsonValue.String value -> Some value
            | _ -> None)

    let private definitive (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value ->
            match Frames.outcome value.Wire with
            | Some "ACCEPTED"
            | Some "ALREADY_ACCEPTED"
            | Some "NOT_ACCEPTED" -> true
            | _ -> false
        | _ -> false

    let rec private prepared
        (access: RemoteAccessSession)
        (token: AccessToken)
        frame
        beforeSend
        cancelled
        =
        task {
            let target: RemoteServiceCall.Access =
                {
                    Service = access.Configuration.Service
                    InstallationId = None
                    Authorization = token.Authorization
                }

            if not (Api.requiresInstallation (Frames.endpoint frame)) then
                return!
                    RemoteServiceCall.send
                        access.Client
                        target
                        frame
                        (fun () -> beforeSend frame)
                        cancelled
            else
                match! RemoteServiceCall.context access.Client target cancelled with
                | Error failure ->
                    return CliReply.local failure |> CliReply.forAttempt frame DeliveryState.NotSent
                | Ok context ->
                    match Frames.bindInstallation context.InstallationId frame with
                    | Error reason ->
                        return
                            CliReply.local reason |> CliReply.forAttempt frame DeliveryState.NotSent
                    | Ok bound ->
                        return! handoff access token target context bound beforeSend cancelled
        }

    and private handoff
        (access: RemoteAccessSession)
        (token: AccessToken)
        (target: RemoteServiceCall.Access)
        (context: ActorContext)
        frame
        beforeSend
        (cancelled: CancellationToken)
        =
        task {
            let current () =
                not cancelled.IsCancellationRequested && access.IsCurrent token

            let input = Frames.input frame

            let identity: RecoveryIdentity =
                {
                    ServiceOrigin = access.Configuration.Service.GetLeftPart(UriPartial.Authority)
                    InstallationId = context.InstallationId
                    ActorId = ActorId.value context.ActorId
                    AttemptId = Frames.attemptId frame
                    OperationId = Frames.operationId frame |> Option.defaultValue Guid.Empty
                    Subject = text "kind" input |> Option.defaultValue "OPERATION"
                    Act =
                        JsonValue.tryMember "act" input
                        |> Option.bind (text "kind")
                        |> Option.defaultValue "RESOLVE"
                }

            let directory =
                RecoveryJournal.location (fun name ->
                    Environment.GetEnvironmentVariable name |> Option.ofObj)

            if not (current ()) then
                return
                    CliReply.local ClientFailure.Cancelled
                    |> CliReply.forAttempt frame DeliveryState.NotSent
            else
                match
                    directory
                    |> Result.bind (fun directory ->
                        RecoveryJournal.save directory identity |> Result.map (fun () -> directory))
                with
                | Error() ->
                    return
                        CliReply.local ClientFailure.RecoveryUnavailable
                        |> CliReply.forAttempt frame DeliveryState.NotSent
                | Ok _ when not (current ()) ->
                    return
                        CliReply.local ClientFailure.Cancelled
                        |> CliReply.forAttempt frame DeliveryState.NotSent
                | Ok directory ->
                    let! reply =
                        RemoteServiceCall.send
                            access.Client
                            { target with
                                InstallationId = Some context.InstallationId
                            }
                            frame
                            (fun () ->
                                cancelled.ThrowIfCancellationRequested()

                                if not (access.IsCurrent token) then
                                    raise (OperationCanceledException())

                                beforeSend frame)
                            cancelled

                    if current () && definitive reply then
                        RecoveryJournal.remove directory identity |> ignore

                    return reply
        }

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
                    | Ok token -> return! prepared access token frame beforeSend cancelled
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
        Invoke: Invocation -> (Invocation -> unit) -> CancellationToken -> Task<CliReply>
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
            let! reply =
                invoke
                    frame
                    (fun prepared ->
                        delivery.Prepare prepared
                        delivery.BeforeDispatch prepared)
                    stopped

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
                Invoke =
                    fun frame beforeSend token -> invoke frame (fun () -> beforeSend frame) token
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
