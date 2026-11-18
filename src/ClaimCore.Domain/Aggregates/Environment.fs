namespace ClaimCore.Domain.Aggregates

open ClaimCore.Domain

[<NoEquality; NoComparison>]
type FamilyEnvironment =
    {
        Requester: AccessView
        RequesterPrincipal: PrincipalKey
        Maintenance: bool
        AssigneeEligible: ActorId -> bool
        ReferenceOwner: CaseReference -> CaseId option
        RelatedCase: CaseSelector -> (CaseId * Lifecycle) option
    }

type PersonEnvironment =
    {
        Requester: AccessView
        RequesterPrincipal: PrincipalKey
        Maintenance: bool
        TargetCases: Map<GrantTarget, CaseId>
        EnabledHumanOwners: int
        ActorCount: int
        TargetPrincipal: PrincipalKey option
    }
