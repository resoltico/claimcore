namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

[<RequireQualifiedAccess>]
type InvalidFrameReason =
    | NotJson
    | DuplicateKey
    | UnknownEndpoint
    | TooLarge
    | Schema

module InvalidFrameReason =
    let vocabulary =
        Vocabulary.ofUnion<InvalidFrameReason>
            "invalid-frame-reason"
            TokenCasing.ScreamingSnake
            None


[<RequireQualifiedAccess>]
type RecoveryRootSetting =
    | XdgStateHome
    | Home

module RecoveryRootSetting =
    let tokens =
        [
            RecoveryRootSetting.XdgStateHome, "XDG_STATE_HOME"
            RecoveryRootSetting.Home, "HOME"
        ]

    let token setting =
        tokens |> List.find (fst >> (=) setting) |> snd

type ClientFailure =
    | InstallationMismatch
    | RecoveryUnavailable
    | RecoveryRootNotAbsolute of RecoveryRootSetting
    | Configuration
    | ConfigurationIssue of CliSetting * ConfigurationIssue
    | Authentication
    | AuthenticationTimeout
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
    | RecoveryRootNotAbsolute of RecoveryRootSetting
    | ConfigurationInvalid
    | ConfigurationIssue of CliSetting * ConfigurationIssue
    | AuthenticationFailed
    | AuthenticationTimeout
    | ServiceUnavailable
    | Cancelled
    | Internal
    | InvalidFrame of InvalidFrameReason
    | InvalidResponse
    | ContractMismatch

[<RequireQualifiedAccess>]
type CliFailureCode =
    | InstallationMismatch
    | RecoveryUnavailable
    | RecoveryRootNotAbsolute
    | ConfigurationInvalid
    | AuthenticationFailed
    | AuthenticationTimeout
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
        | CliFailure.RecoveryRootNotAbsolute _ -> CliFailureCode.RecoveryRootNotAbsolute
        | CliFailure.ConfigurationInvalid
        | CliFailure.ConfigurationIssue _ -> CliFailureCode.ConfigurationInvalid
        | CliFailure.AuthenticationTimeout -> CliFailureCode.AuthenticationTimeout
        | CliFailure.AuthenticationFailed -> CliFailureCode.AuthenticationFailed
        | CliFailure.ServiceUnavailable -> CliFailureCode.ServiceUnavailable
        | CliFailure.Cancelled -> CliFailureCode.Cancelled
        | CliFailure.Internal -> CliFailureCode.Internal
        | CliFailure.InvalidFrame _ -> CliFailureCode.InvalidFrame
        | CliFailure.InvalidResponse -> CliFailureCode.InvalidResponse
        | CliFailure.ContractMismatch -> CliFailureCode.ContractMismatch

    let simple =
        [
            CliFailure.InstallationMismatch
            CliFailure.RecoveryUnavailable
            CliFailure.ConfigurationInvalid
            CliFailure.AuthenticationFailed
            CliFailure.AuthenticationTimeout
            CliFailure.ServiceUnavailable
            CliFailure.Cancelled
            CliFailure.Internal
            CliFailure.InvalidResponse
            CliFailure.ContractMismatch
        ]
        |> List.map (fun failure -> failure, token (ofFailure failure))

module ClientFailure =
    let ofFrame =
        function
        | CliFailure.InstallationMismatch -> ClientFailure.InstallationMismatch
        | CliFailure.RecoveryUnavailable -> ClientFailure.RecoveryUnavailable
        | CliFailure.RecoveryRootNotAbsolute setting ->
            ClientFailure.RecoveryRootNotAbsolute setting
        | CliFailure.ConfigurationInvalid -> ClientFailure.Configuration
        | CliFailure.ConfigurationIssue(setting, issue) ->
            ClientFailure.ConfigurationIssue(setting, issue)
        | CliFailure.AuthenticationTimeout -> ClientFailure.AuthenticationTimeout
        | CliFailure.AuthenticationFailed -> ClientFailure.Authentication
        | CliFailure.ServiceUnavailable -> ClientFailure.Unavailable
        | CliFailure.Cancelled -> ClientFailure.Cancelled
        | CliFailure.Internal -> ClientFailure.Unexpected
        | CliFailure.InvalidFrame _ -> ClientFailure.InvalidFrame
        | CliFailure.InvalidResponse -> ClientFailure.InvalidResponse
        | CliFailure.ContractMismatch -> ClientFailure.ContractMismatch
