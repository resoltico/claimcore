module ClaimCore.Database.Program

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.HostSecurity

[<EntryPoint>]
let main arguments =
    let bytes, code, deliveryUnknown =
        try
            BuildIdentity.requireCompatibleAssembly typeof<Administration>.Assembly

            match Inputs.command (Array.toList arguments) with
            | Error(CommandInputFailure.Usage usage) ->
                let bytes, code = Replies.usage usage in bytes, code, false
            | Error(CommandInputFailure.Operator fault) ->
                let bytes, code = Replies.operatorFault fault in bytes, code, false
            | Ok command ->
                let bytes, code = Execution.run command |> _.GetAwaiter().GetResult()

                let mutates =
                    match command with
                    | Initialize _
                    | Rebuild _
                    | AdmitRestoredInstallation _
                    | Maintenance _ -> true
                    | _ -> false

                bytes,
                code,
                (code = ExitCode.value ExitCode.OutcomeUnknown
                 || (mutates && code = ExitCode.value ExitCode.Success))
        with _ ->
            let bytes, code = Replies.operatorFault OperatorFault.InternalFailure
            bytes, code, false

    try
        use output = StandardSinks.output ()
        Replies.write output (bytes, code, deliveryUnknown)
    with _ ->
        ExitCode.value (
            if deliveryUnknown then
                ExitCode.OutcomeUnknown
            else
                ExitCode.Failed
        )
