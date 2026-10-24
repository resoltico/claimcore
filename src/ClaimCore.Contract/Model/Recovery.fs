namespace ClaimCore.Contract

open System

type RecoveryRequest = { RecoveryId: Guid; OperationId: Guid }
type ForgetRequest = { RecoveryId: Guid }

[<RequireQualifiedAccess>]
type RecoveryOffer =
    | Retry
    | Forget

[<RequireQualifiedAccess>]
type RecoveryAuthorityKind =
    | RegisterActor
    | SetEnabled
    | SetGrant

module RecoveryAuthorityKinds =
    let all =
        [
            RecoveryAuthorityKind.RegisterActor
            RecoveryAuthorityKind.SetEnabled
            RecoveryAuthorityKind.SetGrant
        ]

    let kind =
        function
        | RecoveryAuthorityKind.RegisterActor -> AuthorityKind.RegisterActor
        | RecoveryAuthorityKind.SetEnabled -> AuthorityKind.SetEnabled
        | RecoveryAuthorityKind.SetGrant -> AuthorityKind.SetGrant

    let token value = kind value |> AuthorityKinds.token

    let fromAct =
        function
        | AuthorityAct.RegisterActor -> RecoveryAuthorityKind.RegisterActor
        | AuthorityAct.SetEnabled _ -> RecoveryAuthorityKind.SetEnabled
        | AuthorityAct.SetGrant _ -> RecoveryAuthorityKind.SetGrant

type CaseRecoveryTarget =
    {
        CaseReference: string
        ExpectedRevision: int64
        Act: CaseActKind
    }

type AuthorityRecoveryTarget =
    {
        TargetActorId: Guid
        CaseReference: string option
        Act: RecoveryAuthorityKind
    }

[<RequireQualifiedAccess>]
type RecoverySubject =
    | Case of CaseRecoveryTarget
    | Authority of AuthorityRecoveryTarget

type RecoveryItem =
    {
        RecoveryId: Guid
        OperationId: Guid
        Subject: RecoverySubject
        CreatedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Offers: RecoveryOffer list
    }

    member item.Scope =
        match item.Subject with
        | RecoverySubject.Case _ -> "CASE"
        | RecoverySubject.Authority _ -> "AUTHORITY"

    member item.Act =
        match item.Subject with
        | RecoverySubject.Case target -> CaseActKinds.token target.Act
        | RecoverySubject.Authority target -> RecoveryAuthorityKinds.token target.Act

    member item.CaseReference =
        match item.Subject with
        | RecoverySubject.Case target -> Some target.CaseReference
        | RecoverySubject.Authority target -> target.CaseReference

    member item.ExpectedRevision =
        match item.Subject with
        | RecoverySubject.Case target -> Some target.ExpectedRevision
        | RecoverySubject.Authority _ -> None

    member item.TargetActorId =
        match item.Subject with
        | RecoverySubject.Case _ -> None
        | RecoverySubject.Authority target -> Some target.TargetActorId

type RecoveryPage =
    {
        Items: RecoveryItem list
        NextCursor: string option
    }
