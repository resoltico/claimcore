namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

type ClientFailure =
    | InstallationMismatch
    | RecoveryUnavailable
    | Configuration
    | ConfigurationIssue of CliSetting * ConfigurationIssue
    | Authentication
    | Unavailable
    | Cancelled
    | Unexpected
    | InvalidFrame
    | InvalidResponse
    | ContractMismatch

[<RequireQualifiedAccess>]
type CliFailure =
    | InstallationMismatch
    | RecoveryUnavailable
    | ConfigurationInvalid
    | ConfigurationIssue of CliSetting * ConfigurationIssue
    | AuthenticationFailed
    | ServiceUnavailable
    | Cancelled
    | Internal
    | InvalidFrame
    | InvalidResponse
    | ContractMismatch

[<RequireQualifiedAccess>]
type CliFailureCode =
    | InstallationMismatch
    | RecoveryUnavailable
    | ConfigurationInvalid
    | AuthenticationFailed
    | ServiceUnavailable
    | Cancelled
    | Internal
    | InvalidFrame
    | InvalidResponse
    | ContractMismatch

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CliFailureCode =
    let vocabulary =
        Vocabulary.ofUnion<CliFailureCode> "cli-failure" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary
    let codes = Vocabulary.tokens vocabulary

    let ofFailure =
        function
        | CliFailure.InstallationMismatch -> CliFailureCode.InstallationMismatch
        | CliFailure.RecoveryUnavailable -> CliFailureCode.RecoveryUnavailable
        | CliFailure.ConfigurationInvalid
        | CliFailure.ConfigurationIssue _ -> CliFailureCode.ConfigurationInvalid
        | CliFailure.AuthenticationFailed -> CliFailureCode.AuthenticationFailed
        | CliFailure.ServiceUnavailable -> CliFailureCode.ServiceUnavailable
        | CliFailure.Cancelled -> CliFailureCode.Cancelled
        | CliFailure.Internal -> CliFailureCode.Internal
        | CliFailure.InvalidFrame -> CliFailureCode.InvalidFrame
        | CliFailure.InvalidResponse -> CliFailureCode.InvalidResponse
        | CliFailure.ContractMismatch -> CliFailureCode.ContractMismatch

    let toFailure =
        function
        | CliFailureCode.InstallationMismatch -> CliFailure.InstallationMismatch
        | CliFailureCode.RecoveryUnavailable -> CliFailure.RecoveryUnavailable
        | CliFailureCode.ConfigurationInvalid -> CliFailure.ConfigurationInvalid
        | CliFailureCode.AuthenticationFailed -> CliFailure.AuthenticationFailed
        | CliFailureCode.ServiceUnavailable -> CliFailure.ServiceUnavailable
        | CliFailureCode.Cancelled -> CliFailure.Cancelled
        | CliFailureCode.Internal -> CliFailure.Internal
        | CliFailureCode.InvalidFrame -> CliFailure.InvalidFrame
        | CliFailureCode.InvalidResponse -> CliFailure.InvalidResponse
        | CliFailureCode.ContractMismatch -> CliFailure.ContractMismatch

module ClientFailure =
    let ofFrame =
        function
        | CliFailure.InstallationMismatch -> ClientFailure.InstallationMismatch
        | CliFailure.RecoveryUnavailable -> ClientFailure.RecoveryUnavailable
        | CliFailure.ConfigurationInvalid -> ClientFailure.Configuration
        | CliFailure.ConfigurationIssue(setting, issue) ->
            ClientFailure.ConfigurationIssue(setting, issue)
        | CliFailure.AuthenticationFailed -> ClientFailure.Authentication
        | CliFailure.ServiceUnavailable -> ClientFailure.Unavailable
        | CliFailure.Cancelled -> ClientFailure.Cancelled
        | CliFailure.Internal -> ClientFailure.Unexpected
        | CliFailure.InvalidFrame -> ClientFailure.InvalidFrame
        | CliFailure.InvalidResponse -> ClientFailure.InvalidResponse
        | CliFailure.ContractMismatch -> ClientFailure.ContractMismatch
