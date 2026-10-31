namespace ClaimCore.Cli

open System
open System.Threading
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

/// Recovery after restart resolves a selected local attempt; it never reconstructs a business request.
module internal RecoveryCommand =
    let private failure reason =
        let reply = CliReply.local reason

        {
            Bytes = reply.Bytes
            ExitCode = reply.ExitCode
            MayChange = false
        }

    let private definitive reply =
        match reply.Frame.Result with
        | FrameResult.Reply value ->
            List.contains (Frames.outcome value.Wire) [ Some "ACCEPTED"; Some "NOT_ACCEPTED" ]
        | _ -> false

    let private perform
        (access: RemoteAccessSession)
        (token: AccessToken)
        selection
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
            | Error reply ->
                return
                    {
                        Bytes = reply.Bytes
                        ExitCode = reply.ExitCode
                        MayChange = false
                    }
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
                | Error() -> return failure ClientFailure.RecoveryUnavailable
                | Ok directory ->
                    match RecoveryJournal.list directory scope with
                    | Error() -> return failure ClientFailure.RecoveryUnavailable
                    | Ok records ->
                        match selection with
                        | None ->
                            let bytes = Codec.encode CliSchemas.recoveryList records

                            return
                                {
                                    Bytes = bytes
                                    ExitCode = 0
                                    MayChange = false
                                }
                        | Some attempt ->
                            match
                                records |> List.tryFind (fun record -> record.AttemptId = attempt)
                            with
                            | None -> return failure ClientFailure.InvalidFrame
                            | Some record ->
                                match
                                    Frames.create
                                        Api.operationResolve
                                        {
                                            OperationLookup.OperationId = record.OperationId
                                        }
                                with
                                | Error _ -> return failure ClientFailure.InvalidFrame
                                | Ok frame ->
                                    if
                                        cancelled.IsCancellationRequested
                                        || not (access.IsCurrent token)
                                    then
                                        return failure ClientFailure.Cancelled
                                    else
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
                                                        raise (OperationCanceledException()))
                                                cancelled

                                        if definitive reply && access.IsCurrent token then
                                            RecoveryJournal.remove directory record |> ignore

                                        return
                                            {
                                                Bytes = reply.Bytes
                                                ExitCode = reply.ExitCode
                                                MayChange = true
                                            }
        }

    let run selection cancelled =
        task {
            match RemoteConfiguration.load () with
            | Error reason -> return failure reason
            | Ok configuration ->
                match RemoteAccessSession.Open configuration with
                | Error reason -> return failure reason
                | Ok access ->
                    use access = access

                    match! access.Token cancelled with
                    | Error _ -> return failure ClientFailure.Authentication
                    | Ok token -> return! perform access token selection cancelled
        }
