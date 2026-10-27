namespace ClaimCore.Protocol

open ClaimCore.Domain

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

/// One numeric table for the two executables and their published references.
module ExitCodes =
    let vocabulary = Vocabulary.ofUnion<ExitCode> "exit" TokenCasing.CamelCase None
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let private definition =
        function
        | ExitCode.Success -> 0, true, true
        | ExitCode.Rejected -> 2, true, true
        | ExitCode.Failed -> 3, true, true
        | ExitCode.OutcomeUnknown -> 4, true, true
        | ExitCode.Diverged -> 5, false, true
        | ExitCode.Usage -> 64, true, false
        | ExitCode.InternalError -> 70, true, false
        | ExitCode.Cancelled -> 130, true, false

    let value code =
        let number, _, _ = definition code in number

    let cli code =
        let _, supported, _ = definition code in supported

    let database code =
        let _, _, supported = definition code in supported

    let entries = all |> List.map (fun code -> token code, value code)
