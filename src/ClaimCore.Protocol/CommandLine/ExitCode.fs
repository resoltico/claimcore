namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type ExitCode =
    | Success
    | Rejected
    | Failed
    | OutcomeUnknown
    | Diverged
    | Usage
    | InternalError
    | Cancelled

/// The numeric exit-code contract; each executable owns its supported subset.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ExitCode =
    let vocabulary = Vocabulary.ofUnion<ExitCode> "exit" TokenCasing.CamelCase None
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let value =
        function
        | ExitCode.Success -> 0
        | ExitCode.Rejected -> 2
        | ExitCode.Failed -> 3
        | ExitCode.OutcomeUnknown -> 4
        | ExitCode.Diverged -> 5
        | ExitCode.Usage -> 64
        | ExitCode.InternalError -> 70
        | ExitCode.Cancelled -> 130

    let entries = all |> List.map (fun code -> token code, value code)
