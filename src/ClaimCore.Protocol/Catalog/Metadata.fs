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

[<RequireQualifiedAccess>]
type CatalogOperation =
    | Act of ActKind
    | Query of QueryEndpoint

    member this.Endpoint =
        match this with
        | CatalogOperation.Act _ -> ActorEndpoint.Operation OperationEndpoint.Execute
        | CatalogOperation.Query query -> ActorEndpoint.Query query

    member this.SelectedAct =
        match this with
        | CatalogOperation.Act act -> Some act
        | CatalogOperation.Query _ -> None

    member this.Subject =
        match this with
        | CatalogOperation.Act act -> ActKind.subject act |> ActSubject.token
        | CatalogOperation.Query(QueryEndpoint.CaseGet | QueryEndpoint.CaseList | QueryEndpoint.CaseHistory) ->
            ActSubject.token ActSubject.Case
        | CatalogOperation.Query(QueryEndpoint.AuthorityList | QueryEndpoint.AuthorityGrants) ->
            ActSubject.token ActSubject.Authority
        | CatalogOperation.Query(QueryEndpoint.Observe | QueryEndpoint.Resolve) -> "OPERATION"
        | CatalogOperation.Query QueryEndpoint.Context -> "CONTEXT"

type CatalogEntry =
    {
        LabelId: string
        MeaningId: string
        ConfirmationId: string option
        Kind: CatalogOperation
        Inputs: CatalogInput list
        Constants: BindingAssignment list
    }

    member this.Endpoint = ActorEndpoint.token this.Kind.Endpoint
    member this.Subject = this.Kind.Subject

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InputSource =
    let vocabulary =
        Vocabulary.ofUnion<InputSource> "inputSource" TokenCasing.ScreamingSnake None
