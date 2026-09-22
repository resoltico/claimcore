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
    | InvalidReview
    | InvalidManifest
    | InvalidEvidence
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

type ToolchainIdentity =
    {
        DotnetSdk: string
        Node: string option
        Npm: string option
        PostgreSql: string option
        OperatingSystem: string
        Architecture: string
    }

type PublishFile =
    {
        Path: string
        Length: int64
        Sha256: string
    }

type PublishTreeManifest =
    {
        SchemaVersion: int
        StageId: string
        SourceSha256: string
        LocksSha256: string
        Toolchain: ToolchainIdentity
        Files: PublishFile list
        TreeSha256: string
    }

type SourceIdentity =
    {
        GitRevision: string option
        State: string
        ContentSha256: string
        LocksSha256: string
    }

type StageManifest =
    {
        SchemaVersion: int
        StageId: string
        RunId: string
        Attempt: int
        Outcome: string
        StartedUtc: DateTimeOffset
        FinishedUtc: DateTimeOffset
        Platform: string
        Procedure: string list
        RequiredOutputs: string list
        OutputRoot: string
        Output: PublishTreeManifest
    }

type BlockStatus =
    {
        Id: string
        Document: string
        ExpectedSha256: string
        Status: string
    }

type ContractEvidence =
    { Id: string; PassedTests: string list }

type ReviewSpan =
    {
        Path: string
        BeginMarker: string
        EndMarker: string
    }

type ContractReview =
    {
        ContractId: string
        ReviewerKind: string
        Reviewer: string
        ReviewedOn: DateOnly
        Conclusion: string
        ReviewSubjectHash: string
        WholeFiles: string list
        MarkedSpans: ReviewSpan list
    }

type TestExecution =
    {
        Name: string
        TestId: Guid
        Outcome: string
        Assembly: string
    }

type TestReport =
    {
        Assembly: string
        RunId: Guid
        Total: int
        Passed: int
        Tests: TestExecution list
    }

[<RequireQualifiedAccess>]
type OutputRequirement =
    | Exact of string
    | Suffix of string
    | Prefix of string
    | NativeHostSecurityLibrary

type StageDefinition =
    {
        Id: string
        Producer: string
        AllowedPlatforms: string list
        EvidencePlatform: string option
        Procedure: string list
        RequiredOutputs: OutputRequirement list
    }

type TestReportDefinition =
    {
        StageId: string
        Assembly: string
        FileName: string
        ExpectedTests: int
        ExpectedNames: Set<string>
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
