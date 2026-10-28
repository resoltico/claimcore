namespace ClaimCore.Cli

open ClaimCore.Protocol

open System.Reflection

module CliIdentity =
    let programName = Assembly.GetExecutingAssembly().GetName().Name |> nonNull

    let supportedExitCodes =
        [
            ExitCode.Success
            ExitCode.Rejected
            ExitCode.Failed
            ExitCode.OutcomeUnknown
            ExitCode.Usage
            ExitCode.InternalError
            ExitCode.Cancelled
        ]
