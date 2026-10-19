namespace ClaimCore.Application

open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type AuthorityKind =
    | ProvisionOwner
    | RegisterActor
    | SetGrant
    | SetEnabled

module AuthorityKinds =
    let token =
        function
        | AuthorityKind.ProvisionOwner -> "PROVISION_OWNER"
        | AuthorityKind.RegisterActor -> "REGISTER_ACTOR"
        | AuthorityKind.SetGrant -> "SET_GRANT"
        | AuthorityKind.SetEnabled -> "SET_ENABLED"

    let internal fromRecord =
        function
        | RecordedAuthorityKind.ProvisionOwner -> AuthorityKind.ProvisionOwner
        | RecordedAuthorityKind.RegisterActor -> AuthorityKind.RegisterActor
        | RecordedAuthorityKind.SetGrant -> AuthorityKind.SetGrant
        | RecordedAuthorityKind.SetEnabled -> AuthorityKind.SetEnabled
