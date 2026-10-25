namespace ClaimCore.Application

open ClaimCore.Contract

module AuthorityOperationDefinitions =
    let labelId name = "authority.field." + name + ".label"

    let private input name primitive : AuthorityInputDefinition =
        {
            Name = name
            LabelId = labelId name
            Primitive = primitive
        }

    let target = input "target" AuthorityInputPrimitive.Principal

    let all =
        [
            {
                Kind = AuthorityKind.RegisterActor
                LabelId =
                    Vocabulary.messageKey
                        AuthorityKinds.vocabulary
                        AuthorityKind.RegisterActor
                        "label"
                MeaningId =
                    Vocabulary.messageKey
                        AuthorityKinds.vocabulary
                        AuthorityKind.RegisterActor
                        "meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs = []
            }
            {
                Kind = AuthorityKind.SetGrant
                LabelId =
                    Vocabulary.messageKey AuthorityKinds.vocabulary AuthorityKind.SetGrant "label"
                MeaningId =
                    Vocabulary.messageKey AuthorityKinds.vocabulary AuthorityKind.SetGrant "meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs =
                    [
                        input "role" AuthorityInputPrimitive.Role
                        input "scope" AuthorityInputPrimitive.Scope
                        input "active" AuthorityInputPrimitive.Boolean
                    ]
            }
            {
                Kind = AuthorityKind.SetEnabled
                LabelId =
                    Vocabulary.messageKey AuthorityKinds.vocabulary AuthorityKind.SetEnabled "label"
                MeaningId =
                    Vocabulary.messageKey
                        AuthorityKinds.vocabulary
                        AuthorityKind.SetEnabled
                        "meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs = [ input "enabled" AuthorityInputPrimitive.Boolean ]
            }
        ]
