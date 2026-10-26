namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type AuthorityKind =
    | ProvisionOwner
    | Register
    | Grant
    | Revoke
    | Enable
    | Disable

module AuthorityKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            [
                AuthorityKind.ProvisionOwner
                AuthorityKind.Register
                AuthorityKind.Grant
                AuthorityKind.Revoke
                AuthorityKind.Enable
                AuthorityKind.Disable
            ]
            (function
             | AuthorityKind.ProvisionOwner -> "PROVISION_OWNER"
             | AuthorityKind.Register -> "REGISTER"
             | AuthorityKind.Grant -> "GRANT"
             | AuthorityKind.Revoke -> "REVOKE"
             | AuthorityKind.Enable -> "ENABLE"
             | AuthorityKind.Disable -> "DISABLE")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

/// The complete act vocabulary combines the disjoint case and authority subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | Authority of AuthorityKind

module ActKinds =
    let vocabulary =
        Vocabulary.define
            "act"
            ((CaseActKinds.all |> List.map ActKind.Case)
             @ (AuthorityKinds.all |> List.map ActKind.Authority))
            (function
             | ActKind.Case value -> CaseActKinds.token value
             | ActKind.Authority value -> AuthorityKinds.token value)

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
