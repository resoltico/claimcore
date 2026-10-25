namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type AuthorityKind =
    | ProvisionOwner
    | RegisterActor
    | SetGrant
    | SetEnabled

module AuthorityKinds =
    let vocabulary =
        Vocabulary.define
            "authority"
            [
                AuthorityKind.ProvisionOwner, "PROVISION_OWNER"
                AuthorityKind.RegisterActor, "REGISTER_ACTOR"
                AuthorityKind.SetGrant, "SET_GRANT"
                AuthorityKind.SetEnabled, "SET_ENABLED"
            ]

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
