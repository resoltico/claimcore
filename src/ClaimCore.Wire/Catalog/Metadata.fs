namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

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
        Vocabulary: string option
        RequiresParent: bool
    }

[<RequireQualifiedAccess>]
type BindingKind =
    | Text
    | Integer
    | Boolean
    | Choice of vocabulary: string * options: string list
    | Choices of vocabulary: string * options: string list
    | Group of vocabulary: string * options: string list

module BindingKind =
    let vocabulary =
        function
        | BindingKind.Choices(name, _)
        | BindingKind.Choice(name, _)
        | BindingKind.Group(name, _) -> Some name
        | BindingKind.Text
        | BindingKind.Integer
        | BindingKind.Boolean -> None

type CatalogInput =
    {
        Identity: string
        Field: InputTarget option
        Path: string list
        Kind: BindingKind
        AdviceGroup: RecordGroup option
        Branches: BindingCondition list
        LabelId: string
        HintId: string option
        RuleId: string option
        Order: int
        Prefill: string list option
        Required: bool
        WireRequired: bool
        AllowsNull: bool
        Default: BindingValue option
        DiagnosticTargets: string list
        Source: InputSource
    }

/// One bounded record collection. Row paths and conditions are relative to a single item.
type CatalogCollection =
    {
        Identity: string
        Field: InputTarget
        Path: string list
        Branches: BindingCondition list
        LabelId: string
        HintId: string option
        RuleId: string option
        Order: int
        Required: bool
        MinimumItems: int
        MaximumItems: int
        RowInputs: CatalogInput list
        RowConstants: BindingAssignment list
    }

[<RequireQualifiedAccess>]
type CatalogSubject =
    | Work
    | Reminder
    | Note
    | Case
    | Authority
    | Operation
    | Context

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CatalogSubject =
    let vocabulary =
        Vocabulary.ofUnion<CatalogSubject> "catalogSubject" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type CatalogOperation =
    | Act of ActKind
    | Query of QueryTaskId

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
        | CatalogOperation.Act(ActKind.Case _)
        | CatalogOperation.Query(QueryTaskId.CasePayments | QueryTaskId.CaseAssignees | QueryTaskId.CaseGet | QueryTaskId.CaseList | QueryTaskId.CaseSummary | QueryTaskId.CaseTimeline) ->
            CatalogSubject.Case
        | CatalogOperation.Act(ActKind.Work _)
        | CatalogOperation.Query QueryTaskId.WorkViews -> CatalogSubject.Work
        | CatalogOperation.Act(ActKind.Note _)
        | CatalogOperation.Query QueryTaskId.CaseNotes -> CatalogSubject.Note
        | CatalogOperation.Act(ActKind.Reminder _)
        | CatalogOperation.Query(QueryTaskId.ReminderList | QueryTaskId.ReminderGet) ->
            CatalogSubject.Reminder
        | CatalogOperation.Act(ActKind.Authority _)
        | CatalogOperation.Query(QueryTaskId.AuthorityList | QueryTaskId.AuthorityGrants | QueryTaskId.AuthorityHistory | QueryTaskId.AuthorityRequests | QueryTaskId.AuthorityRequestsDismiss) ->
            CatalogSubject.Authority
        | CatalogOperation.Query QueryTaskId.OperationGet -> CatalogSubject.Operation
        | CatalogOperation.Query QueryTaskId.Context -> CatalogSubject.Context

[<RequireQualifiedAccess>]
type InteractionEffect =
    | Work
    | Reminder
    | Note
    | Read
    | Case of CaseEffect
    | Authority

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InteractionEffect =
    let vocabulary =
        Vocabulary.define
            "interactionEffect"
            ([
                InteractionEffect.Read
                InteractionEffect.Work
                InteractionEffect.Reminder
                InteractionEffect.Note
                InteractionEffect.Authority
             ]
             @ (Vocabulary.all CaseEffect.vocabulary |> List.map InteractionEffect.Case))
            (function
             | InteractionEffect.Read -> "READ"
             | InteractionEffect.Work -> "CHANGE_WORK"
             | InteractionEffect.Note -> "CHANGE_NOTE"
             | InteractionEffect.Reminder -> "CHANGE_REMINDER"
             | InteractionEffect.Authority -> "CHANGE_AUTHORITY"
             | InteractionEffect.Case effect -> CaseEffect.token effect)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CatalogOperation =
    let interaction (operation: CatalogOperation) =
        match operation.SelectedAct, operation.Endpoint with
        | Some(ActKind.Case kind), _ ->
            let description = CaseActKind.describe kind

            Some description.Family,
            description.AffectedSections,
            InteractionEffect.Case description.Effect
        | Some(ActKind.Work _), _ ->
            Some ActFamily.Business, [ CaseSection.Work ], InteractionEffect.Work
        | Some(ActKind.Note _), _ -> Some ActFamily.Business, [], InteractionEffect.Note
        | Some(ActKind.Reminder _), _ ->
            Some ActFamily.Business, [ CaseSection.Work ], InteractionEffect.Reminder
        | Some(ActKind.Authority _), _ -> None, [], InteractionEffect.Authority
        | _ -> None, [], InteractionEffect.Read

    let consequence =
        function
        | CatalogOperation.Act act -> Consequence.ofAct act
        | CatalogOperation.Query query -> QueryConsequence.ofQuery query


type CatalogEntry =
    {
        LabelId: string
        MeaningId: string
        ConsequenceId: string option
        DoneId: string option
        Family: ActFamily option
        AffectedSections: CaseSection list
        Effect: InteractionEffect
        Consequence: Consequence
        Kind: CatalogOperation
        Inputs: CatalogInput list
        Collections: CatalogCollection list
        Constants: BindingAssignment list
    }

    member this.Endpoint = ActorEndpoint.token this.Kind.Endpoint
    member this.Subject = this.Kind.Subject

/// Static consumer metadata is encoded from this model, not inferred from an example JSON value.
type FieldPresentationDefinition =
    {
        Name: BusinessField
        ValueKind: FieldValueKind
        Group: RecordGroup option
        Section: CaseSection option
        Pairs: BusinessField option
        LabelId: string
        HintId: string
        RuleId: string
        Scalar: ScalarRule
        AllowsAbsence: bool
        Prominence: Prominence
        Listed: bool
    }

type InputPresentationDefinition =
    {
        Name: string
        LabelId: string
        HintId: string option
        RuleId: string option
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
        OperationWindowMilliseconds: int
        OperationFutureAllowanceMilliseconds: int
    }

type RolePresentationDefinition =
    {
        Role: Role
        MeaningId: string
        AdmittedPrincipalKinds: PrincipalKind list
        AdmittedScopes: ScopeKind list
    }

type CapabilityPresentationDefinition =
    {
        Capability: Capability
        AdmittedPrincipalKinds: PrincipalKind list
        AdmittedScopes: ScopeKind list
    }

type CurrencyMinorUnitDefinition = { Currency: string; MinorUnits: int }

type CatalogDocument =
    {
        Fields: FieldPresentationDefinition list
        InputFields: InputPresentationDefinition list
        CharacterWarnings: CharacterWarningDefinition
        Limits: PresentationLimits
        Entries: CatalogEntry list
        Roles: RolePresentationDefinition list
        Capabilities: CapabilityPresentationDefinition list
        ProductVersion: string
        MoneyMinorUnits: CurrencyMinorUnitDefinition list
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InputSource =
    let vocabulary =
        Vocabulary.ofUnion<InputSource> "inputSource" TokenCasing.ScreamingSnake None
