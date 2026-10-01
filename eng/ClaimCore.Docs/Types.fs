namespace ClaimCore.Docs

open System

[<RequireQualifiedAccess>]
type DiagnosticCode =
    | Invocation
    | UnsafePath
    | InvalidMarkdown
    | GeneratedDrift
    | InvalidLink
    | InvalidContract
    | InvalidManifest
    | ConcurrentEdit
    | Unexpected

[<NoEquality; NoComparison>]
type Diagnostic =
    {
        Code: DiagnosticCode
        Path: string option
        Line: int option
        Message: string
    }

[<RequireQualifiedAccess>]
module Diagnostic =
    let create code message =
        {
            Code = code
            Path = None
            Line = None
            Message = message
        }

    let at path line code message =
        {
            Code = code
            Path = Some path
            Line = Some line
            Message = message
        }

[<NoEquality; NoComparison>]
type ProcessRequest =
    {
        FileName: string
        Arguments: string list
        WorkingDirectory: string
        Environment: (string * string option) list
        Timeout: TimeSpan
    }

[<NoEquality; NoComparison>]
type ProcessOutput =
    {
        ExitCode: int
        StandardOutput: string
        StandardError: string
    }

type IProcessRunner =
    abstract Run: ProcessRequest -> Result<ProcessOutput, string>

type BlockStatus =
    {
        Id: string
        Document: string
        ExpectedSha256: string
        Status: string
    }

[<RequireQualifiedAccess>]
module ExitCode =
    [<Literal>]
    let Success = 0

    [<Literal>]
    let CheckFailed = 2

    [<Literal>]
    let InvocationFailed = 3

    [<Literal>]
    let Unexpected = 70
