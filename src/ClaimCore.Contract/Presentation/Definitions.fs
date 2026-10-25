namespace ClaimCore.Contract

open System

/// Permanent text representability and syntax. Policy is never a generic schema assertion.
type TextFormatConstraints =
    {
        MinimumCharacters: int
        Grammar: string option
    }

type TextAdmissionPolicy =
    {
        MaximumCharacters: int
        RequiresNonBlank: bool
        RejectsSurroundingWhitespace: bool
        RejectsControlCharacters: bool
        RejectsForbiddenInvisibleCharacters: bool
    }

type ScalarTextConstraints =
    {
        Format: TextFormatConstraints
        Policy: TextAdmissionPolicy option
    }

/// Exact Gregorian calendar-date input rules.
type CalendarDateScalarRule =
    {
        ExactFormat: string
        Minimum: DateOnly
        Maximum: DateOnly
    }

/// Exact non-negative decimal-text input rules.
type AmountScalarRule =
    {
        Text: ScalarTextConstraints
        Grammar: string
        MaximumIntegerDigits: int
        MaximumFractionalDigits: int
    }

/// Exact uppercase ASCII currency-token input rules.
type CurrencyScalarRule =
    {
        Text: ScalarTextConstraints
        Grammar: string
        ExactCharacters: int
    }

/// Catalog supplies closed status vocabulary; Domain owns transitions.
type CaseStatusScalarRule = { AllowedValues: CaseStatus list }

/// Typed scalar rules are the Domain authority for adapter-visible field constraints.
[<RequireQualifiedAccess>]
type ScalarRule =
    | CalendarDate of CalendarDateScalarRule
    | Text of ScalarTextConstraints
    | Amount of AmountScalarRule
    | Currency of CurrencyScalarRule
    | CaseStatus of CaseStatusScalarRule

/// A act form may begin blank or reuse a current business-field value as a suggestion.
[<RequireQualifiedAccess>]
type PrefillSource =
    | Blank
    | CurrentField of field: BusinessField

/// Ordered authored scalar metadata. The immutable case target is never an input field.
type FieldInputDefinition =
    {
        FieldName: InputTarget
        Prefill: PrefillSource
    }

/// An explicit operator choice for one correction group. The tag is part of the request shape;
/// it is not inferred from omitted or nullable scalar fields.
/// One required grouped correction input. ReplaceFields are required only for REPLACE. KEEP reads
/// the authoritative accepted value and CLEAR deliberately removes a value that is present.
type CorrectionGroupDefinition =
    {
        Name: CorrectionGroup
        LabelId: string
        MeaningId: string
        Actions: CorrectionGroupAction list
        ReplaceFields: FieldInputDefinition list
    }

/// Acts either collect one ordered scalar object or explicitly collect required correction
/// groups. This is the semantic source for every adapter; it avoids optional flat correction fields.
[<RequireQualifiedAccess>]
type ActInputShape =
    | Fields of FieldInputDefinition list
    | CorrectionGroups of CorrectionGroupDefinition list


/// Semantic operation descriptions, not widgets or client-owned transition rules.
type ActDefinition =
    {
        Kind: CaseActKind
        LabelId: string
        MeaningId: string
        ConfirmationId: string
        Inputs: ActInputShape
    }


/// The closed input vocabulary required by authority operations.
[<RequireQualifiedAccess>]
type AuthorityInputPrimitive =
    | Principal
    | Role
    | Scope
    | Boolean

type AuthorityInputDefinition =
    {
        Name: string
        LabelId: string
        Primitive: AuthorityInputPrimitive
    }

type AuthorityOperationDefinition =
    {
        Kind: AuthorityKind
        LabelId: string
        MeaningId: string
        ConfirmationId: string
        Inputs: AuthorityInputDefinition list
    }


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
        Prefill: BusinessField option
        DiagnosticTargets: string list
        Source: InputSource
    }

type CatalogEntry =
    {
        Scope: string
        Kind: string
        Inputs: CatalogInput list
        Constants: BindingAssignment list
    }

/// Static consumer metadata is encoded from this model, not inferred from an example JSON value.
type FieldPresentationDefinition =
    {
        Name: BusinessField
        LabelId: string
        MeaningId: string
        Scalar: ScalarRule
        AllowsAbsence: bool
    }

type InputPresentationDefinition =
    {
        Name: InputTarget
        LabelId: string
        MeaningId: string
        Scalar: ScalarRule
    }

type ProtocolFieldDefinition = { Name: string; LabelId: string }

type CharacterWarningDefinition =
    {
        MaximumCodePoints: int
        Categories: string list
    }

type RoleDefinition =
    {
        Role: Role
        Capabilities: Capability list
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
        Acts: ActDefinition list
        InputFields: InputPresentationDefinition list
        AuthorityTarget: AuthorityInputDefinition
        AuthorityActs: AuthorityOperationDefinition list
        AuthorityFields: ProtocolFieldDefinition list
        CharacterWarnings: CharacterWarningDefinition
        Statuses: CaseStatus list
        RejectionDiagnostics: DiagnosticDefinition list
        FaultDiagnostics: DiagnosticDefinition list
        Roles: RoleDefinition list
        Limits: PresentationLimits
        Entries: CatalogEntry list
        ProductVersion: string
    }
