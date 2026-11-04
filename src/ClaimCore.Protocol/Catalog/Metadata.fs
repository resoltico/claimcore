namespace ClaimCore.Protocol

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
        | CatalogOperation.Act(ActKind.Case _)
        | CatalogOperation.Query(QueryEndpoint.CaseAssignees | QueryEndpoint.CaseGet | QueryEndpoint.CaseList | QueryEndpoint.CaseViews | QueryEndpoint.CaseSummary | QueryEndpoint.CaseHistory | QueryEndpoint.CaseActivity | QueryEndpoint.CaseTimeline) ->
            CatalogSubject.Case
        | CatalogOperation.Act(ActKind.Work _)
        | CatalogOperation.Query QueryEndpoint.WorkOverview -> CatalogSubject.Work
        | CatalogOperation.Act(ActKind.Reminder _)
        | CatalogOperation.Query(QueryEndpoint.ReminderList | QueryEndpoint.ReminderGet | QueryEndpoint.ReminderViews) ->
            CatalogSubject.Reminder
        | CatalogOperation.Act(ActKind.Authority _)
        | CatalogOperation.Query(QueryEndpoint.AuthorityList | QueryEndpoint.AuthorityGrants | QueryEndpoint.AuthorityHistory | QueryEndpoint.AuthorityRequests | QueryEndpoint.AuthorityRequestsDismiss) ->
            CatalogSubject.Authority
        | CatalogOperation.Query(QueryEndpoint.Observe | QueryEndpoint.Status | QueryEndpoint.Resolve) ->
            CatalogSubject.Operation
        | CatalogOperation.Query QueryEndpoint.Context -> CatalogSubject.Context

[<RequireQualifiedAccess>]
type InteractionEffect =
    | Work
    | Reminder
    | Read
    | Case of CaseEffect
    | Authority
    | FinalNonAcceptance

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module InteractionEffect =
    let vocabulary =
        Vocabulary.define
            "interactionEffect"
            ([
                InteractionEffect.Read
                InteractionEffect.Work
                InteractionEffect.Reminder
                InteractionEffect.Authority
                InteractionEffect.FinalNonAcceptance
             ]
             @ (Vocabulary.all CaseEffect.vocabulary |> List.map InteractionEffect.Case))
            (function
             | InteractionEffect.Read -> "READ"
             | InteractionEffect.Work -> "CHANGE_WORK"
             | InteractionEffect.Reminder -> "CHANGE_REMINDER"
             | InteractionEffect.Authority -> "CHANGE_AUTHORITY"
             | InteractionEffect.FinalNonAcceptance -> "FINAL_NON_ACCEPTANCE"
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
        | Some(ActKind.Reminder _), _ ->
            Some ActFamily.Business, [ CaseSection.Work ], InteractionEffect.Reminder
        | Some(ActKind.Authority _), _ -> None, [], InteractionEffect.Authority
        | None, ActorEndpoint.Query QueryEndpoint.Resolve ->
            None, [], InteractionEffect.FinalNonAcceptance
        | _ -> None, [], InteractionEffect.Read

    let consequence =
        function
        | CatalogOperation.Act act -> Consequence.ofAct act
        | CatalogOperation.Query query -> Consequence.ofQuery query


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
    }

type RolePresentationDefinition =
    {
        Role: Role
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
