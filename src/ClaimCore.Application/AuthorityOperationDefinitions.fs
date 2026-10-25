namespace ClaimCore.Application

open ClaimCore.Contract

module AuthorityOperationDefinitions =
    let labelId name = "input." + name + ".label"

    let private input name primitive : AuthorityInputDefinition =
        {
            Name = name
            LabelId = labelId name
            Primitive = primitive
        }

    let target = input "target" AuthorityInputPrimitive.Principal

    let all =
        [
            AuthorityKind.Register
            AuthorityKind.Grant
            AuthorityKind.Revoke
            AuthorityKind.Enable
            AuthorityKind.Disable
        ]
        |> List.map (fun kind ->
            {
                Kind = kind
                LabelId = Vocabulary.messageKey AuthorityKinds.vocabulary kind "label"
                MeaningId = Vocabulary.messageKey AuthorityKinds.vocabulary kind "meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs =
                    match kind with
                    | AuthorityKind.Grant
                    | AuthorityKind.Revoke ->
                        [
                            input "role" AuthorityInputPrimitive.Role
                            input "scope" AuthorityInputPrimitive.Scope
                        ]
                    | _ -> []
            })
