namespace ClaimCore.Application

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
        Label: string
        Primitive: AuthorityInputPrimitive
    }

type AuthorityOperationDefinition =
    {
        Kind: AuthorityKind
        Label: string
        Meaning: string
        Inputs: AuthorityInputDefinition list
    }

module AuthorityOperationDefinitions =
    let private input name label primitive =
        {
            Name = name
            Label = label
            Primitive = primitive
        }

    let target = input "target" "Actor principal" AuthorityInputPrimitive.Principal

    let all =
        [
            {
                Kind = AuthorityKind.RegisterActor
                Label = "Register actor"
                Meaning =
                    "Register a principal without granting case or administration capabilities."
                Inputs = []
            }
            {
                Kind = AuthorityKind.SetGrant
                Label = "Set actor grant"
                Meaning = "Grant or revoke a role at installation or case scope."
                Inputs =
                    [
                        input "role" "Role" AuthorityInputPrimitive.Role
                        input "scope" "Grant scope" AuthorityInputPrimitive.Scope
                        input "active" "Grant active" AuthorityInputPrimitive.Boolean
                    ]
            }
            {
                Kind = AuthorityKind.SetEnabled
                Label = "Set actor enabled"
                Meaning =
                    "Enable or disable an actor. The final enabled human owner remains protected."
                Inputs = [ input "enabled" "Actor enabled" AuthorityInputPrimitive.Boolean ]
            }
        ]
