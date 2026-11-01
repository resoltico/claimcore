module ClaimCore.Database.Program

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

open System
open ClaimCore.Application
open ClaimCore.Postgres

[<EntryPoint>]
let main arguments =
    let bytes, code, deliveryUnknown =
        try
            BuildIdentity.requireCompatibleAssembly typeof<Administration>.Assembly

            match Inputs.command (Array.toList arguments) with
            | Error usage -> let bytes, code = Replies.usage usage in bytes, code, false
            | Ok command ->
                let bytes, code = Execution.run command |> _.GetAwaiter().GetResult()

                let mutates =
                    match command with
                    | Initialize _
                    | ProvisionOwner
                    | Rebuild _
                    | InvalidateRestoredSessions
                    | Maintenance _
                    | PruneSessions _ -> true
                    | _ -> false

                bytes,
                code,
                (code = ExitCode.value ExitCode.OutcomeUnknown
                 || (mutates && code = ExitCode.value ExitCode.Success))
        with _ ->
            let bytes, code = Replies.failed CoreFault.InternalError
            bytes, code, false

    try
        Replies.write (Console.OpenStandardOutput()) (bytes, code, deliveryUnknown)
    with _ ->
        ExitCode.value (
            if deliveryUnknown then
                ExitCode.OutcomeUnknown
            else
                ExitCode.Failed
        )
