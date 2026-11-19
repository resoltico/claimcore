namespace ClaimCore.Cli

open ClaimCore.CommandLine

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation

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

/// Already-authenticated transport inputs; tests can replace HTTP without bypassing routing/recovery.
[<NoEquality; NoComparison>]
type internal AuthenticatedFrameAccess =
    {
        Configuration: RemoteConfiguration
        Client: System.Net.Http.HttpClient
        IsCurrent: AccessToken -> bool
        Clock: TimeProvider
        JournalDirectory: unit -> Result<string, ClientFailure>
    }

module private FrameInvocation =
    let private definitive (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value ->
            match Frames.outcome value.Wire with
            | Some "ACCEPTED"
            | Some "ALREADY_ACCEPTED"
            | Some "NOT_ACCEPTED" -> true
            | _ -> false
        | _ -> false

    let rec prepared
        transferClient
        (access: AuthenticatedFrameAccess)
        (token: AccessToken)
        frame
        beforeSend
        consent
        cancelled
        =
        task {
            let target: RemoteServiceCall.Access =
                {
                    Service = access.Configuration.Service
                    InstallationId = None
                    Authorization = token.Authorization
                }

            if
                not (Api.requiresInstallation (Frames.endpoint frame))
                && (Frames.endpoint frame).Id
                   <> EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview)
            then
                return!
                    RemoteServiceCall.send
                        access.Client
                        target
                        frame
                        (fun () -> beforeSend frame)
                        cancelled
            else
                match! RemoteServiceCall.context access.Client target cancelled with
                | Error reply -> return reply |> CliReply.forAttempt frame DeliveryState.NotSent
                | Ok context ->
                    match Frames.bindInstallation context.InstallationId frame with
                    | Error reason ->
                        return
                            CliReply.local reason |> CliReply.forAttempt frame DeliveryState.NotSent
                    | Ok _ when
                        consent
                        |> Option.exists (fun confirm ->
                            not (confirm access.Configuration.Mode context))
                        ->
                        return
                            CliReply.local ClientFailure.Cancelled
                            |> CliReply.forAttempt frame DeliveryState.NotSent
                    | Ok bound ->
                        let bound =
                            if
                                (Frames.endpoint bound).Id =
                                    EndpointId.Actor(
                                        ActorEndpoint.Operation OperationEndpoint.Preview
                                    )
                            then
                                Frames.freeze bound
                            else
                                bound

                        match Api.effect (Frames.endpoint bound) with
                        | EndpointEffect.Read
                        | EndpointEffect.OperationalWrite ->
                            return!
                                RemoteServiceCall.send
                                    access.Client
                                    { target with
                                        InstallationId = Some context.InstallationId
                                    }
                                    bound
                                    (fun () -> beforeSend bound)
                                    cancelled
                        | EndpointEffect.LedgerExecution ->
                            return!
                                handoff
                                    transferClient
                                    access
                                    token
                                    target
                                    context
                                    bound
                                    beforeSend
                                    cancelled

        }

    and private handoff
        transferClient
        (access: AuthenticatedFrameAccess)
        (token: AccessToken)
        (target: RemoteServiceCall.Access)
        (context: ActorContext)
        frame
        beforeSend
        (cancelled: CancellationToken)
        =
        task {
            let frame = Frames.refreshProvisional access.Clock Guid.CreateVersion7 frame

            let current () =
                not cancelled.IsCancellationRequested && access.IsCurrent token

            let kind =
                Frames.value Api.operationExecute frame
                |> Option.map (fun (input: OperationSubmission) -> input.Task)
                |> Option.defaultWith (fun () ->
                    invalidOp "A mutation handoff requires its admitted typed operation.")

            let identity: RecoveryIdentity =
                {
                    ServiceOrigin = access.Configuration.Service.GetLeftPart(UriPartial.Authority)
                    InstallationId = context.InstallationId
                    ActorId = ActorId.value context.ActorId
                    AttemptId = Frames.attemptId frame
                    OperationId = Frames.operationId frame |> Option.defaultValue Guid.Empty
                    Task = TaskId.token kind
                    Refused = false
                }

            let directory = access.JournalDirectory()

            if not (current ()) then
                return
                    CliReply.local ClientFailure.Cancelled
                    |> CliReply.forAttempt frame DeliveryState.NotSent
            else
                match
                    directory
                    |> Result.bind (fun directory ->
                        RecoveryJournal.save directory identity
                        |> Result.map (fun () -> directory)
                        |> Result.mapError (fun () -> ClientFailure.RecoveryUnavailable))
                with
                | Error problem ->
                    return CliReply.local problem |> CliReply.forAttempt frame DeliveryState.NotSent
                | Ok _ when not (current ()) ->
                    return
                        CliReply.local ClientFailure.Cancelled
                        |> CliReply.forAttempt frame DeliveryState.NotSent
                | Ok directory ->
                    let client =
                        if (Frames.upload frame).IsSome then
                            transferClient
                        else
                            access.Client

                    let! reply =
                        RemoteServiceCall.send
                            client
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

                    if current () then
                        if definitive reply then
                            RecoveryJournal.remove directory identity |> ignore
                        else
                            let refused =
                                match reply.Frame.Result with
                                | FrameResult.Reply value -> value.Class = ReplyKind.Rejected
                                | FrameResult.HostFailure failure ->
                                    failure.Phase = Some ExecutionPhase.NotStarted
                                | FrameResult.LocalFailure _ -> false

                            if reply.Frame.Delivery = DeliveryState.Delivered && refused then
                                RecoveryJournal.markRefused directory identity |> ignore

                    return reply
        }

    let private authenticated
        (holder: RemoteSessionHolder)
        (cancelled: CancellationToken)
        reject
        run
        =
        task {
            if cancelled.IsCancellationRequested then
                return reject ClientFailure.Cancelled
            elif not CliIdentity.supportsRemote then
                return reject ClientFailure.Configuration
            else
                match holder.Acquire() with
                | Error failure -> return reject failure
                | Ok access ->
                    match! access.Token cancelled with
                    | Error _ when cancelled.IsCancellationRequested ->
                        return reject ClientFailure.Cancelled
                    | Error AccessFailure.OidcCredentialUnavailable ->
                        return
                            reject (
                                ClientFailure.ConfigurationIssue(
                                    CliSetting.SecretFile,
                                    ConfigurationIssue.PrivateInputRefused
                                )
                            )
                    | Error AccessFailure.OidcInteractionTimeout ->
                        return reject ClientFailure.AuthenticationTimeout
                    | Error _ -> return reject ClientFailure.Authentication
                    | Ok token -> return! run access token
        }

    let invoke holder frame beforeSend consent cancelled =
        authenticated holder cancelled CliReply.local (fun access token ->
            let transport =
                {
                    Configuration = access.Configuration
                    Client = access.Client
                    IsCurrent = access.IsCurrent
                    Clock = TimeProvider.System
                    JournalDirectory =
                        fun () ->
                            RecoveryJournal.location (fun name ->
                                Environment.GetEnvironmentVariable name |> Option.ofObj)
                }

            prepared access.TransferClient transport token frame beforeSend consent cancelled)

    let private expectedAttachment (selection: AttachmentReadRequest) (listed: CliReply) =
        match listed.Frame.Result with
        | FrameResult.Reply reply ->
            match ServiceReply.value Api.caseAttachments reply with
            | Some(RepresentationReply.Data(Outcome.Succeeded page)) ->
                let caseMatches =
                    match selection.Target with
                    | CaseSelector.Id caseId -> caseId = page.CaseId
                    | CaseSelector.Reference _ -> true

                if not caseMatches then
                    Error(AttachmentDownloadFailure.Local ClientFailure.InvalidResponse)
                else
                    match
                        page.Items
                        |> List.tryFind (fun row -> row.AttachmentId = selection.AttachmentId)
                    with
                    | Some row ->
                        let exact =
                            { selection with
                                Target = CaseSelector.Id page.CaseId
                            }

                        Ok(exact, row.Name)
                    | None ->
                        Refusal.Access AccessRefusal.ResourceUnavailable
                        |> HumanReplies.rejection
                        |> AttachmentTransferFailure.Rejected
                        |> AttachmentDownloadFailure.Refused
                        |> Error
            | Some(RepresentationReply.Data _) -> Error(AttachmentDownloadFailure.Access listed)
            | _ -> Error(AttachmentDownloadFailure.Local ClientFailure.InvalidResponse)
        | _ -> Error(AttachmentDownloadFailure.Access listed)

    let download holder (selection: AttachmentReadRequest) (cancelled: CancellationToken) =
        let local = AttachmentDownloadFailure.Local >> Error

        authenticated holder cancelled local (fun access token ->
            task {
                let target: RemoteServiceCall.Access =
                    {
                        Service = access.Configuration.Service
                        InstallationId = None
                        Authorization = token.Authorization
                    }

                let current () =
                    not cancelled.IsCancellationRequested && access.IsCurrent token

                match! RemoteServiceCall.context access.Client target cancelled with
                | Error reply -> return Error(AttachmentDownloadFailure.Access reply)
                | Ok _ when not (current ()) -> return local ClientFailure.Cancelled
                | Ok context ->
                    let bound =
                        { target with
                            InstallationId = Some context.InstallationId
                        }

                    match Frames.create Api.caseAttachments { Target = selection.Target } with
                    | Error _ -> return local ClientFailure.InvalidFrame
                    | Ok request ->
                        let! listed =
                            RemoteServiceCall.send access.Client bound request ignore cancelled

                        if not (current ()) then
                            return local ClientFailure.Cancelled
                        else
                            match expectedAttachment selection listed with
                            | Error refusal -> return Error refusal
                            | Ok(exact, name) ->
                                let! result =
                                    AttachmentBinaryCall.download
                                        access.TransferClient
                                        bound
                                        exact
                                        name
                                        cancelled

                                return if current () then result else local ClientFailure.Cancelled
            })

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
        Invoke:
            Invocation
                -> (Invocation -> unit)
                -> (RemoteClientMode -> ActorContext -> bool) option
                -> CancellationToken
                -> Task<CliReply>
        Download:
            AttachmentReadRequest
                -> CancellationToken
                -> Task<Result<byte array, AttachmentDownloadFailure>>
        Discard: CancellationToken -> Task<unit>
        Owned: IDisposable
    }

module private RemoteTransport =
    let create configuration =
        let holder = new RemoteSessionHolder(configuration)

        {
            Invoke = FrameInvocation.invoke holder
            Download = FrameInvocation.download holder
            Discard = holder.Discard
            Owned = holder
        }

type RemoteFrameProcessor
    private (streams: RemoteStreams, language, render, transport: RemoteTransport) =
    let input = streams.Input

    let delivery =
        ClaimCore.Cli.FrameDelivery(streams.Output, streams.Errors, language, render)

    let invoke = transport.Invoke

    let exchangePrepared frame consent stopped =
        delivery.Prepare frame

        task {
            let mutable dispatched = frame

            let! reply =
                invoke
                    frame
                    (fun prepared ->
                        dispatched <- prepared
                        delivery.Prepare prepared
                        delivery.BeforeDispatch prepared)
                    consent
                    stopped

            return dispatched, CliReply.forAttempt dispatched reply.Frame.Delivery reply
        }

    let exchange frame consent stopped =
        task { let! _, reply = exchangePrepared frame consent stopped in return reply }

    let interpret frame stopped =
        match frame with
        | InputFrame.EndOfInput -> None
        | InputFrame.TooLarge ->
            Some(CliReply.protocol (CliFailure.InvalidFrame InvalidFrameReason.TooLarge))
        | InputFrame.Bytes bytes ->
            match Frames.admitWithReason bytes with
            | Error reason -> Some(CliReply.protocol (CliFailure.InvalidFrame reason))
            | Ok frame ->
                Some(exchange frame None stopped |> fun value -> value.GetAwaiter().GetResult())

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
        new RemoteFrameProcessor(
            input,
            output,
            errors,
            language,
            (fun (reply: CliReply) -> reply.Bytes)
        )

    static member internal ForShell(streams, configuration) =
        new RemoteFrameProcessor(
            streams,
            "en",
            (fun (reply: CliReply) -> reply.Bytes),
            RemoteTransport.create (Some configuration)
        )

    static member internal ForTest(input, output, errors, invoke, discard, ?download) =
        new RemoteFrameProcessor(
            {
                Input = input
                Output = output
                Errors = errors
            },
            "en",
            (fun (reply: CliReply) -> reply.Bytes),
            {
                Invoke =
                    fun frame beforeSend consent token ->
                        match consent with
                        | None -> invoke frame (fun () -> beforeSend frame) token
                        | Some _ ->
                            Task.FromResult(
                                CliReply.local ClientFailure.Configuration
                                |> CliReply.forAttempt frame DeliveryState.NotSent
                            )
                Download =
                    defaultArg download (fun _ _ ->
                        Task.FromResult(
                            Error(AttachmentDownloadFailure.Local ClientFailure.Configuration)
                        ))
                Discard = discard
                Owned =
                    { new IDisposable with
                        member _.Dispose() = ()
                    }
            }
        )

    static member internal ForAuthenticatedTest
        (streams, access: AuthenticatedFrameAccess, token: AccessToken)
        =
        new RemoteFrameProcessor(
            streams,
            "en",
            (fun (reply: CliReply) -> reply.Bytes),
            {
                Invoke =
                    fun frame beforeSend consent cancelled ->
                        FrameInvocation.prepared
                            access.Client
                            access
                            token
                            frame
                            beforeSend
                            consent
                            cancelled
                Download =
                    fun _ _ ->
                        Task.FromResult(
                            Error(AttachmentDownloadFailure.Local ClientFailure.Configuration)
                        )
                Discard = fun _ -> Task.FromResult()
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
        exchange frame None stopped

    member internal _.ExchangePrepared(frame, stopped) =
        delivery.BeginFrame()
        exchangePrepared frame None stopped

    member internal _.Download(selection, cancelled) = transport.Download selection cancelled

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
