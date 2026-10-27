namespace ClaimCore.Protocol

open ClaimCore.Domain

/// Exact declared member paths bind semantic inputs to one selected wire branch.
type BindingCondition = { Path: string list; Value: string }

[<RequireQualifiedAccess>]
type BindingValue =
    | Text of string
    | Boolean of bool
    | Integer of int64

type BindingAssignment =
    {
        Path: string list
        Value: BindingValue
        Branches: BindingCondition list
    }

[<RequireQualifiedAccess>]
type BindingKind =
    | Text
    | Integer
    | Boolean
    | Choice of string list
    | Group of string list

[<RequireQualifiedAccess>]
type InputSource =
    | Authored
    | OperationIdentity
    | ExpectedRevision

type CatalogInput =
    {
        Identity: string
        Field: InputTarget option
        Path: string list
        Kind: BindingKind
        Branches: BindingCondition list
        LabelId: string
        MeaningId: string option
        Order: int
        Prefill: string list option
        Required: bool
        Default: BindingValue option
        DiagnosticTargets: string list
        Source: InputSource
    }

type CatalogEntry =
    {
        Endpoint: string
        LabelId: string
        MeaningId: string
        ConfirmationId: string option
        Synopsis: string
        Subject: string
        Kind: string
        Inputs: CatalogInput list
        Constants: BindingAssignment list
    }

/// Static consumer metadata is encoded from this model, not inferred from an example JSON value.
type FieldPresentationDefinition =
    {
        Name: BusinessField
        Group: RecordGroup option
        LabelId: string
        MeaningId: string
        Scalar: ScalarRule
        AllowsAbsence: bool
    }

type InputPresentationDefinition =
    {
        Name: string
        LabelId: string
        MeaningId: string option
        Scalar: ScalarRule option
    }

type CharacterWarningDefinition =
    {
        MaximumCodePoints: int
        Categories: string list
    }

type PresentationLimits =
    {
        DefaultPageSize: int
        MaximumPageSize: int
        RequestByteLimit: int
    }

type CatalogDocument =
    {
        Fields: FieldPresentationDefinition list
        InputFields: InputPresentationDefinition list
        CharacterWarnings: CharacterWarningDefinition
        Limits: PresentationLimits
        Entries: CatalogEntry list
        ProductVersion: string
    }
