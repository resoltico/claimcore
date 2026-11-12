namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

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
        Roles: RolePresentationDefinition list
        Capabilities: CapabilityPresentationDefinition list
        ProductVersion: string
        MoneyMinorUnits: CurrencyMinorUnitDefinition list
    }
