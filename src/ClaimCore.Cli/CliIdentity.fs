namespace ClaimCore.Cli

open ClaimCore.Wire
open ClaimCore.Presentation

open System.Reflection

module CliIdentity =
    let supportsRemote =
        System.OperatingSystem.IsMacOS() || System.OperatingSystem.IsLinux()

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
