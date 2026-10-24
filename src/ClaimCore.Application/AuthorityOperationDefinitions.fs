namespace ClaimCore.Application

open ClaimCore.Contract

/// The closed input vocabulary required by authority operations.
[<RequireQualifiedAccess>]
type AuthorityInputPrimitive =
    | Principal
    | Role
    | Scope
    | Boolean

type AuthorityInputDefinition =
    {
        Name: string
        LabelId: string
        Primitive: AuthorityInputPrimitive
    }

type AuthorityOperationDefinition =
    {
        Kind: AuthorityKind
        LabelId: string
        MeaningId: string
        ConfirmationId: string
        Inputs: AuthorityInputDefinition list
    }

module AuthorityOperationDefinitions =
    let private input name label primitive =
        {
            Name = name
            LabelId = label
            Primitive = primitive
        }

    let target =
        input "target" "authority.field.target.label" AuthorityInputPrimitive.Principal

    let all =
        [
            {
                Kind = AuthorityKind.RegisterActor
                LabelId = "authority.REGISTER_ACTOR.label"
                MeaningId = "authority.REGISTER_ACTOR.meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs = []
            }
            {
                Kind = AuthorityKind.SetGrant
                LabelId = "authority.SET_GRANT.label"
                MeaningId = "authority.SET_GRANT.meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs =
                    [
                        input "role" "authority.field.role.label" AuthorityInputPrimitive.Role
                        input "scope" "authority.field.scope.label" AuthorityInputPrimitive.Scope
                        input
                            "active"
                            "authority.field.active.label"
                            AuthorityInputPrimitive.Boolean
                    ]
            }
            {
                Kind = AuthorityKind.SetEnabled
                LabelId = "authority.SET_ENABLED.label"
                MeaningId = "authority.SET_ENABLED.meaning"
                ConfirmationId = "ui.confirmExactRequest"
                Inputs =
                    [
                        input
                            "enabled"
                            "authority.field.enabled.label"
                            AuthorityInputPrimitive.Boolean
                    ]
            }
        ]
