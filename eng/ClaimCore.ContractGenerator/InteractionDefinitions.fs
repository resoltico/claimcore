namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application

/// Explicit bindings are joined to semantic definitions here, never guessed by a client.
module InteractionDefinitions =
    let private assignment path value : BindingAssignment = { Path = path; Value = value }

    let private text path value =
        assignment path (BindingValue.Text value)

    let private condition path value : BindingCondition = { Path = path; Value = value }
    let private flag name = Some(InvocationArgument.Flag name)

    let private input path kind label argument : InteractionInput =
        {
            Identity = String.concat "." path
            Field = None
            Path = path
            Kind = kind
            Branches = []
            LabelId = label
            MeaningId = None
            Order = 0
            Prefill = None
            DiagnosticTargets = []
            Source = InputSource.Authored
            Argument = argument
            WhenPresent = []
        }

    let private identity =
        { input
              [ "operationId" ]
              BindingKind.Text
              "authority.field.operationId.label"
              (flag "operation-id") with
            Source = InputSource.OperationIdentity
            DiagnosticTargets = [ "operationId" ]
        }

    let private expected =
        { input
              [ "expectedRevision" ]
              BindingKind.Integer
              "authority.field.expectedRevision.label"
              (flag "expected-revision") with
            Source = InputSource.ExpectedRevision
            DiagnosticTargets = [ "expectedRevision" ]
        }

    let private reference =
        { input
              [ "caseReference" ]
              BindingKind.Text
              (FieldTable.labelId BusinessField.CaseReference)
              (Some(InvocationArgument.Positional 0)) with
            Field = Some(InputTarget.Field BusinessField.CaseReference)
            Prefill = Some BusinessField.CaseReference
            DiagnosticTargets = [ "caseReference" ]
        }

    let private scalar prefix branches (field: FieldInputDefinition) =
        let token = InputTargets.token field.FieldName

        let label, meaning =
            match field.FieldName with
            | InputTarget.Field field -> FieldTable.labelId field, FieldTable.meaningId field
            | InputTarget.Reason -> "field.reason.label", "field.reason.meaning"
            | _ -> invalidOp "An authored scalar needs a semantic presentation."

        { input (prefix @ [ token ]) BindingKind.Text label (flag (Grammar.kebab token)) with
            Field = Some field.FieldName
            Branches = branches
            MeaningId = Some meaning
            DiagnosticTargets = [ token ]
            Prefill =
                match field.Prefill with
                | PrefillSource.Blank -> None
                | PrefillSource.CurrentField field -> Some field
        }

    let private group (group: CorrectionGroupDefinition) =
        let name = CorrectionGroup.token group.Name
        let path = [ "act"; "groups"; name; "mode" ]

        let choice =
            { input
                  path
                  (BindingKind.Group(List.map CorrectionActions.token group.Actions))
                  group.LabelId
                  (flag name) with
                MeaningId = Some group.MeaningId
                DiagnosticTargets = [ name ]
            }

        choice
        :: List.map
            (scalar [ "act"; "groups"; name; "values" ] [ condition path "REPLACE" ])
            group.ReplaceFields

    let private caseInputs (command: CommandDefinition) =
        match command.Inputs with
        | CommandInputShape.CorrectionGroups groups -> List.collect group groups
        | CommandInputShape.Fields fields ->
            let prefix =
                match command.Kind with
                | CaseActKind.Stewardship StewardshipActKind.Void
                | CaseActKind.Stewardship StewardshipActKind.Reinstate -> [ "act" ]
                | _ -> [ "act"; "values" ]

            List.map (scalar prefix []) fields

    let private ordered values =
        List.mapi (fun index value -> { value with Order = index }) values

    let private caseDefinition (command: CommandDefinition) : InteractionDefinition =
        let kind = CaseActKinds.token command.Kind
        let isOpen = command.Kind = CaseActKind.Business BusinessActKind.Open

        {
            Scope = OperationScopes.Case
            Kind = kind
            Inputs =
                ordered (
                    [ identity; reference ]
                    @ (if isOpen then [] else [ expected ])
                    @ caseInputs command
                )
            Constants =
                [ text [ "scope" ] OperationScopes.Case; text [ "act"; "kind" ] kind ]
                @ (if isOpen then
                       [ assignment [ "expectedRevision" ] (BindingValue.Integer 0L) ]
                   else
                       [])
            Invocations =
                [
                    {
                        Verb = Grammar.caseVerb kind
                        Constants = []
                    }
                ]
        }

    let private target =
        [
            input
                [ "target"; "kind" ]
                (BindingKind.Choice(PrincipalKinds.all))
                "authority.field.kind.label"
                (flag "kind")
            { input
                  [ "target"; "issuer" ]
                  BindingKind.Text
                  "authority.field.issuer.label"
                  (flag "issuer") with
                DiagnosticTargets = [ "principal.issuer" ]
            }
            { input
                  [ "target"; "subject" ]
                  BindingKind.Text
                  "authority.field.subject.label"
                  (flag "subject") with
                DiagnosticTargets = [ "principal.subject" ]
            }
        ]

    let private authorityInput (field: AuthorityInputDefinition) =
        match field.Primitive with
        | AuthorityInputPrimitive.Principal -> invalidOp "Principal binds its declared members."
        | AuthorityInputPrimitive.Role ->
            [
                input
                    [ "act"; "role" ]
                    (BindingKind.Choice(List.map RoleTokens.token RoleTokens.all))
                    field.LabelId
                    (flag "role")
            ]
        | AuthorityInputPrimitive.Boolean ->
            [ input [ "act"; field.Name ] BindingKind.Boolean field.LabelId None ]
        | AuthorityInputPrimitive.Scope ->
            [
                input
                    [ "act"; "scope"; "kind" ]
                    (BindingKind.Group [ "INSTALLATION"; "CASE" ])
                    field.LabelId
                    None
                { input
                      [ "act"; "scope"; "caseReference" ]
                      BindingKind.Text
                      (FieldTable.labelId BusinessField.CaseReference)
                      (flag "case") with
                    Branches = [ condition [ "act"; "scope"; "kind" ] "CASE" ]
                    WhenPresent = [ text [ "act"; "scope"; "kind" ] "CASE" ]
                }
            ]

    let private authorityAlias kind verb : InvocationDefinition =
        let constants =
            match kind with
            | AuthorityKind.SetEnabled ->
                [ assignment [ "act"; "enabled" ] (BindingValue.Boolean(verb = "enable")) ]
            | AuthorityKind.SetGrant ->
                [
                    assignment [ "act"; "active" ] (BindingValue.Boolean(verb = "grant"))
                    text [ "act"; "scope"; "kind" ] "INSTALLATION"
                ]
            | _ -> []

        { Verb = verb; Constants = constants }

    let private authorityDefinition
        (command: AuthorityOperationDefinition)
        : InteractionDefinition =
        let kind = AuthorityKinds.token command.Kind

        {
            Scope = OperationScopes.Authority
            Kind = kind
            Inputs = ordered (identity :: target @ List.collect authorityInput command.Inputs)
            Constants = [ text [ "scope" ] OperationScopes.Authority; text [ "act"; "kind" ] kind ]
            Invocations = Grammar.authorityVerbs kind |> List.map (authorityAlias command.Kind)
        }

    let all =
        List.map caseDefinition CommandDefinitions.all
        @ List.map authorityDefinition AuthorityOperationDefinitions.all
