namespace ClaimCore.Domain.Tasks

open ClaimCore.Domain

/// Shared declaration constructors express input structure; each task owns its chosen inputs and rules.
module internal Declarations =
    let target value = TaskInputId.Target value
    let field value = target (InputTarget.Field value)

    let required input =
        TaskInputDeclaration.create TaskInputMode.Required input

    let optional input =
        TaskInputDeclaration.create TaskInputMode.Optional input

    let retained input =
        { input with
            Retention = InputRetention.Retained
        }

    let bound input =
        { retained input with
            Source = InputSource.Binding
        }

    let operation = required (target InputTarget.OperationId)
    let caseTarget = required (field BusinessField.CaseReference) |> bound
    let revision = required (target InputTarget.ExpectedRevision)

    let group mode group =
        BusinessField.ofGroup group
        |> List.map (fun field ->
            let selected =
                if
                    mode = TaskInputMode.Required && (BusinessField.describe field).AllowsAbsence
                then
                    TaskInputMode.Optional
                else
                    mode

            { TaskInputDeclaration.create selected (TaskInputId.Target(InputTarget.Field field)) with
                Clearable =
                    mode = TaskInputMode.Update && (BusinessField.describe field).AllowsAbsence
            })

    let create token aggregate target creates family consequence capabilities inputs collections =
        {
            Id = TaskId.ofLiteral token
            Exposure = TaskExposure.Client
            Aggregate = aggregate
            Target = target
            Creates = creates
            Family = family
            Consequence = consequence
            ReviewsFamilySequence = false
            Capabilities = capabilities
            Inputs = inputs
            Collections = collections
        }

    let family token target creates family consequence capabilities inputs =
        create token AggregateKind.Family target creates family consequence capabilities inputs []

    let signInIdentity =
        [ InputTarget.PrincipalIssuer; InputTarget.PrincipalSubject ]
        |> List.map (target >> required)

    let principal = required (target InputTarget.PrincipalKind) :: signInIdentity

    let profile =
        [
            required (target InputTarget.ProfileDisplayName)
            optional (target InputTarget.ProfileContact)
        ]

    let grantSelection =
        [
            required (target InputTarget.Role)
            required TaskInputId.Scope
            optional (field BusinessField.CaseReference)
            optional TaskInputId.CaseId
        ]
        |> List.map retained

    // Admission constraints belong to assigning roles, not selecting stored roles for removal.
    let grantAdmission =
        grantSelection
        |> List.map (fun input ->
            if input.Input <> target InputTarget.Role then
                input
            else
                { input with
                    ChoicesWhen =
                        [
                            {
                                Input = TaskInputId.Scope
                                Value = ScopeKind.token ScopeKind.Case
                                Choices =
                                    Role.all
                                    |> List.filter (fun role ->
                                        List.contains ScopeKind.Case (Access.roleScopes role))
                                    |> List.map Role.token
                            }
                            {
                                Input = target InputTarget.PrincipalKind
                                Value = PrincipalKind.serviceToken
                                Choices =
                                    Role.all
                                    |> List.filter (Access.humanOnlyRole >> not)
                                    |> List.map Role.token
                            }
                        ]
                })

    let authored declaration =
        (declaration.Inputs
         |> List.filter (fun input -> input.Source = InputSource.Authored)
         |> List.map _.Input)
        @ (declaration.Collections
           |> List.map (fun collection -> TaskCollectionId.input collection.Collection))

    let availability declaration blocked =
        {
            Seeds = Map.empty
            Selections = Set.empty
            Editable = authored declaration
            Choices = Map.empty
            Blocked = blocked
            Recommendation = None
        }
