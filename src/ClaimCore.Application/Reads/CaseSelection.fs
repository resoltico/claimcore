namespace ClaimCore.Application.Reads

open ClaimCore.Domain
open ClaimCore.Application.Contracts

type internal CaseSelection =
    {
        DateBasis: WorkDateBasis
        Visibility: CaseVisibility
        ActorId: ActorId
        ReadRoles: Role list
        ForbiddenRoles: Role list
        Filter: CaseListFilter<CaseSearchText>
    }
