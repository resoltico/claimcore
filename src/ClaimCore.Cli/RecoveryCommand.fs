namespace ClaimCore.Cli

open System
open System.Threading
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json

[<NoEquality; NoComparison>]
type internal RecoveryCommandResult =
    {
        Bytes: byte array
        ExitCode: int
        MayChange: bool
    }

/// Recovery after restart checks or deliberately settles a selected local attempt; it never reconstructs a business request.
module internal RecoveryCommand =
    let private reply (options: RecoveryInvocation) mayChange (value: CliReply) =
        {
            Bytes =
                if options.Text then
                    TextReply.bytes options.Language value.Frame
                else
                    Array.append value.Bytes [| byte '\n' |]
            ExitCode = value.ExitCode
            MayChange = mayChange
        }

    let private failure options reason =
        reply options false (CliReply.local reason)

    let private listing (options: RecoveryInvocation) records =
        let bytes =
            if not options.Text then
                Array.append (Codec.encode CliSchemas.recoveryList records) [| byte '\n' |]
            else
                let rows =
                    match records with
                    | [] -> [ MessageCatalogs.renderCli options.Language CliMessage.RecoveryEmpty ]
                    | records ->
                        records
                        |> List.map (fun (record: RecoveryIdentity) ->
                            MessageCatalogs.renderCli
                                options.Language
                                (CliMessage.RecoveryAttempt(
                                    record.AttemptId.ToString("D"),
                                    TerminalText.data record.Subject,
                                    TerminalText.data record.Act,
                                    record.OperationId.ToString("D")
                                )))

                Encoding.UTF8.GetBytes(String.concat "\n" rows + "\n")

        {
            Bytes = bytes
            ExitCode = 0
            MayChange = false
        }

    let private consent (console: InteractiveConsole) language mode action context operation =
        match action, mode with
        | RecoveryAttemptAction.Check, _
        | RecoveryAttemptAction.Settle, RemoteClientMode.Automation -> true
        | RecoveryAttemptAction.Settle, RemoteClientMode.Interactive ->
            CliConfirmation.settlement console language context operation

    let private definitive reply =
        match reply.Frame.Result with
        | FrameResult.Reply value ->
            List.contains (Frames.outcome value.Wire) [ Some "ACCEPTED"; Some "NOT_ACCEPTED" ]
        | _ -> false

    let private perform
        (access: RemoteAccessSession)
        (token: AccessToken)
        (options: RecoveryInvocation)
        console
        (cancelled: CancellationToken)
        =
        task {
            let target: RemoteServiceCall.Access =
                {
                    Service = access.Configuration.Service
                    InstallationId = None
                    Authorization = token.Authorization
                }

            match! RemoteServiceCall.context access.Client target cancelled with
            | Error failureReply -> return reply options false failureReply
            | Ok context ->
                let scope: RecoveryScope =
                    {
                        ServiceOrigin = target.Service.GetLeftPart(UriPartial.Authority)
                        InstallationId = context.InstallationId
                        ActorId = ActorId.value context.ActorId
                    }

                match
                    RecoveryJournal.location (fun name ->
                        Environment.GetEnvironmentVariable name |> Option.ofObj)
                with
                | Error() -> return failure options ClientFailure.RecoveryUnavailable
                | Ok directory ->
                    match RecoveryJournal.list directory scope with
                    | Error() -> return failure options ClientFailure.RecoveryUnavailable
                    | Ok records ->
                        match options.Action with
                        | RecoveryAction.List -> return listing options records
                        | RecoveryAction.Attempt(action, attempt) ->
                            match
                                records |> List.tryFind (fun record -> record.AttemptId = attempt)
                            with
                            | None -> return failure options ClientFailure.InvalidFrame
                            | Some record when
                                not (
                                    consent
                                        console
                                        options.Language
                                        access.Configuration.Mode
                                        action
                                        context
                                        record.OperationId
                                )
                                ->
                                return failure options ClientFailure.Cancelled
                            | Some record ->
                                let input =
                                    {
                                        OperationLookup.OperationId = record.OperationId
                                    }

                                let frame =
                                    match action with
                                    | RecoveryAttemptAction.Check ->
                                        Frames.create Api.operationStatus input
                                    | RecoveryAttemptAction.Settle ->
                                        Frames.create Api.operationResolve input

                                match frame with
                                | Error _ -> return failure options ClientFailure.InvalidFrame
                                | Ok frame ->
                                    if
                                        cancelled.IsCancellationRequested
                                        || not (access.IsCurrent token)
                                    then
                                        return failure options ClientFailure.Cancelled
                                    else
                                        let! response =
                                            RemoteServiceCall.send
                                                access.Client
                                                { target with
                                                    InstallationId = Some context.InstallationId
                                                }
                                                frame
                                                (fun () ->
                                                    cancelled.ThrowIfCancellationRequested()

                                                    if not (access.IsCurrent token) then
                                                        raise (OperationCanceledException()))
                                                cancelled

                                        if definitive response && access.IsCurrent token then
                                            match RecoveryJournal.remove directory record with
                                            | Ok() -> ()
                                            | Error() ->
                                                try
                                                    MessageCatalogs.renderCli
                                                        options.Language
                                                        CliMessage.RecoveryCleanupUnavailable
                                                    |> fun text ->
                                                        Encoding.UTF8.GetBytes(text + "\n")
                                                    |> console.Write
                                                with
                                                | :? IO.IOException
                                                | :? ObjectDisposedException -> ()

                                        return
                                            reply
                                                options
                                                (action = RecoveryAttemptAction.Settle
                                                 && response.Frame.Delivery <> DeliveryState.NotSent)
                                                response
        }

    let run options console cancelled =
        task {
            try
                match RemoteConfiguration.load () with
                | Error reason -> return failure options reason
                | Ok configuration ->
                    match RemoteAccessSession.Open configuration with
                    | Error reason -> return failure options reason
                    | Ok access ->
                        use access = access

                        match! access.Token cancelled with
                        | Error _ -> return failure options ClientFailure.Authentication
                        | Ok token -> return! perform access token options console cancelled
            with :? OperationCanceledException ->
                return failure options ClientFailure.Cancelled
        }
