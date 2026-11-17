namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

type CaseReferenceItem =
    {
        CaseId: CaseId
        Reference: CaseReference
    }

/// Current labels are supplied by the authorized snapshot, independently of historical values.
type ChangeReferences =
    {
        Actors: ActorRef list
        Cases: CaseReferenceItem list
    }

[<RequireQualifiedAccess>]
type ReviewTarget =
    | Case of CaseId * CaseReference
    | Person of ActorRef option
