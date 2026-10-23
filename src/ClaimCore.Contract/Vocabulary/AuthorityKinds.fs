namespace ClaimCore.Contract

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
