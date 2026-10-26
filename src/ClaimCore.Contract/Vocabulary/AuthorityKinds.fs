namespace ClaimCore.Contract

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
                AuthorityKind.ProvisionOwner, "PROVISION_OWNER"
                AuthorityKind.Register, "REGISTER"
                AuthorityKind.Grant, "GRANT"
                AuthorityKind.Revoke, "REVOKE"
                AuthorityKind.Enable, "ENABLE"
                AuthorityKind.Disable, "DISABLE"
            ]

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
            ((Vocabulary.entries CaseActKinds.vocabulary
              |> List.map (fun (kind, token) -> ActKind.Case kind, token))
             @ (Vocabulary.entries AuthorityKinds.vocabulary
                |> List.map (fun (kind, token) -> ActKind.Authority kind, token)))

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
