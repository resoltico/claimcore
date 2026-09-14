// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

[<NoComparison>]
type CaseFields =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
        CaseReference: string
        PaymentDecisionDate: (string) option
        PayableAmount: (string) option
        PayableCurrency: (string) option
        PaymentDate: (string) option
        Status: string
    }

[<NoComparison>]
type CaseView =
    { Fields: CaseFields; Revision: string }

[<NoComparison>]
type FieldDiff =
    {
        FieldName: string
        Before: (string) option
        After: (string) option
    }

[<NoComparison>]
type RuntimeContext =
    {
        ProductVersion: string
        EffectiveBusinessDate: string
        TimeZoneId: string
    }

[<NoComparison>]
type AdvisoryReview =
    {
        Before: (CaseView) option
        Proposed: CaseView
        Changes: (FieldDiff) list
        Context: RuntimeContext
        Advisory: bool
    }

[<NoComparison>]
type CaseSummary =
    {
        CaseReference: string
        Revision: string
        Status: string
    }

[<NoComparison>]
type ChangeSummary =
    {
        OperationId: string
        Revision: string
        Command: string
        RecordedAt: string
        RecordedBy: string
    }

[<NoComparison>]
type CommandInputDescriptorBlank = { FieldName: string; Prefill: string }

[<NoComparison>]
type CommandInputDescriptorCurrentField =
    {
        FieldName: string
        Prefill: string
        CurrentField: string
    }

[<RequireQualifiedAccess; NoComparison>]
type CommandInputDescriptor =
    | Blank of CommandInputDescriptorBlank
    | CurrentField of CommandInputDescriptorCurrentField

[<NoComparison>]
type CommandDescriptor =
    {
        Kind: string
        Label: string
        Meaning: string
        Inputs: (CommandInputDescriptor) list
    }

[<NoComparison>]
type CurrentCase =
    {
        Case: CaseView
        AvailableCommands: (string) list
    }

[<NoComparison>]
type Receipt =
    {
        OperationId: string
        Snapshot: CaseView
        RecordedAt: string
        RecordedBy: string
        Replayed: bool
        Command: string
    }

[<NoComparison>]
type DefiniteExecutionAccepted = { Tag: string; Receipt: Receipt }

[<NoComparison>]
type Rejection =
    {
        Code: string
        Message: string
        Field: (string) option
        ActualRevision: (string) option
        RecommendedAction: string
    }

[<NoComparison>]
type DefiniteExecutionRejected =
    {
        Tag: string
        OperationId: string
        Rejection: Rejection
    }

[<NoComparison>]
type Fault =
    {
        Code: string
        Message: string
        RecommendedAction: string
    }

[<NoComparison>]
type DefiniteExecutionFailedBeforeCommit =
    {
        Tag: string
        OperationId: string
        Fault: Fault
    }

[<RequireQualifiedAccess; NoComparison>]
type DefiniteExecution =
    | Accepted of DefiniteExecutionAccepted
    | Rejected of DefiniteExecutionRejected
    | FailedBeforeCommit of DefiniteExecutionFailedBeforeCommit

[<NoComparison>]
type FieldDescriptorScalarText =
    {
        Kind: string
        MinimumCharacters: int64
        MaximumCharacters: int64
        RequiresNonBlank: bool
        RejectsSurroundingWhitespace: bool
        RejectsControlCharacters: bool
        RequiresWellFormedUnicode: bool
    }

[<NoComparison>]
type FieldDescriptorScalarCalendarDate =
    {
        Kind: string
        ExactFormat: string
        Minimum: string
        Maximum: string
    }

[<NoComparison>]
type FieldDescriptorScalarAmount =
    {
        Kind: string
        Grammar: string
        MaximumIntegerDigits: int64
        MaximumFractionalDigits: int64
    }

[<NoComparison>]
type FieldDescriptorScalarCurrency =
    {
        Kind: string
        Grammar: string
        ExactCharacters: int64
    }

[<NoComparison>]
type FieldDescriptorScalarCaseStatus =
    {
        Kind: string
        AllowedValues: (string) list
    }

[<RequireQualifiedAccess; NoComparison>]
type FieldDescriptorScalar =
    | Text of FieldDescriptorScalarText
    | CalendarDate of FieldDescriptorScalarCalendarDate
    | Amount of FieldDescriptorScalarAmount
    | Currency of FieldDescriptorScalarCurrency
    | CaseStatus of FieldDescriptorScalarCaseStatus

[<NoComparison>]
type FieldDescriptor =
    {
        Name: string
        NativeName: string
        Label: string
        Meaning: string
        AllowsAbsence: bool
        Scalar: FieldDescriptorScalar
    }

[<NoComparison>]
type SemanticDefinitionRulesItem =
    {
        Identifier: string
        Category: string
        Meaning: string
    }

[<NoComparison>]
type SemanticDefinition =
    {
        ContractKind: string
        Application: string
        Scope: string
        CanonicalCommandFormat: int64
        RequestFingerprintVersion: int64
        RecoveryEnvelopeFormat: int64
        DefaultPageSize: int64
        MaximumPageSize: int64
        RequestByteLimit: int64
        Fields: (FieldDescriptor) list
        Commands: (CommandDescriptor) list
        Statuses: (string) list
        Rules: (SemanticDefinitionRulesItem) list
    }

[<NoComparison>]
type DefinitionPayload =
    {
        SemanticFingerprint: string
        WebFingerprint: string
        Runtime: RuntimeContext
        Definition: SemanticDefinition
    }

[<NoComparison>]
type HistoryEntrySummary = { Tag: string; Change: ChangeSummary }

[<NoComparison>]
type HistoryEntryFull = { Tag: string; Receipt: Receipt }

[<RequireQualifiedAccess; NoComparison>]
type HistoryEntry =
    | Summary of HistoryEntrySummary
    | Full of HistoryEntryFull

[<NoComparison>]
type HostFailure =
    {
        Kind: string
        Code: string
        Message: string
        ExecutionPhase: (string) option
    }
