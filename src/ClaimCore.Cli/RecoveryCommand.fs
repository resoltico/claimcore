namespace ClaimCore.Cli

open System
open System.Threading
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

[<NoEquality; NoComparison>]
type internal RecoveryCommandResult =
    {
        Bytes: byte array
        ExitCode: int
        MayChange: bool
    }

/// Checks identity-only journal entries until the service gives a final answer.
module internal RecoveryCommand =
    let private reply (options: RecoveryInvocation) mayChange (value: CliReply) =
        {
            Bytes =
                if options.Text then
                    ReplyText.bytes options.Language value.Frame
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
                                    TerminalText.data (ActSubject.token record.Subject),
                                    TerminalText.data record.Act,
                                    record.OperationId.ToString("D")
                                )))

                Encoding.UTF8.GetBytes(String.concat "\n" rows + "\n")

        {
            Bytes = bytes
            ExitCode = 0
            MayChange = false
        }

    let private perform
        (access: RemoteAccessSession)
        (token: AccessToken)
        (options: RecoveryInvocation)
        (console: InteractiveConsole)
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
                        | RecoveryAction.Attempt(RecoveryAttemptAction.Check, attempt) ->
                            match
                                records |> List.tryFind (fun record -> record.AttemptId = attempt)
                            with
                            | None -> return failure options ClientFailure.InvalidFrame
                            | Some record ->
                                let frame =
                                    Frames.create
                                        Api.operationGet
                                        {
                                            OperationLookup.OperationId = record.OperationId
                                        }

                                match frame with
                                | Error _ -> return failure options ClientFailure.InvalidFrame
                                | Ok frame ->
                                    let frame =
                                        if options.Text then
                                            Frames.withRepresentation Representation.Human frame
                                        else
                                            frame

                                    let mutable completed = None

                                    while completed.IsNone do
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

                                        let outcome =
                                            match response.Frame.Result with
                                            | FrameResult.Reply value -> Some value.Class.Kind
                                            | _ -> None

                                        match outcome with
                                        | Some ReplyKind.Pending when
                                            console.IsInteractive
                                            && access.Configuration.Mode =
                                                RemoteClientMode.Interactive
                                            ->
                                            console.Write (reply options false response).Bytes
                                            do! System.Threading.Tasks.Task.Delay(1000, cancelled)
                                        | Some(ReplyKind.Accepted | ReplyKind.NotAccepted) ->
                                            if access.IsCurrent token then
                                                match RecoveryJournal.remove directory record with
                                                | Ok() -> ()
                                                | Error() ->
                                                    MessageCatalogs.renderCli
                                                        options.Language
                                                        CliMessage.RecoveryCleanupUnavailable
                                                    |> fun text ->
                                                        Encoding.UTF8.GetBytes(text + "\n")
                                                    |> console.Write

                                            completed <- Some response
                                        | _ -> completed <- Some response

                                    let response = completed |> Option.get
                                    return reply options false response
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
