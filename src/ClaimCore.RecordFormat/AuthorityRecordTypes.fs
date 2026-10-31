namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type RecordedGrantScope =
    | Installation
    | Case of CaseId * CaseReference

type RecordedGrant = Grant<RecordedGrantScope>

type AuthorityRequestRecord =
    {
        OperationId: Guid
        Target: PrincipalInput
        Act: AuthorityAct<RecordedGrant>
    }
