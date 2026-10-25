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

    let private input path kind label : InteractionInput =
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
        }

    let private identity =
        { input
              [ "operationId" ]
              BindingKind.Text
              (AuthorityOperationDefinitions.labelId "operationId") with
            Source = InputSource.OperationIdentity
            DiagnosticTargets = [ "operationId" ]
        }

    let private expected =
        { input
              [ "expectedRevision" ]
              BindingKind.Integer
              (AuthorityOperationDefinitions.labelId "expectedRevision") with
            Source = InputSource.ExpectedRevision
            DiagnosticTargets = [ "expectedRevision" ]
        }

    let private reference =
        { input
              [ (FieldTable.key BusinessField.CaseReference) ]
              BindingKind.Text
              (FieldTable.labelId BusinessField.CaseReference) with
            Field = Some(InputTarget.Field BusinessField.CaseReference)
            Prefill = Some BusinessField.CaseReference
            DiagnosticTargets = [ (FieldTable.key BusinessField.CaseReference) ]
        }

    let private scalar prefix branches (field: FieldInputDefinition) =
        let token = InputTargets.token field.FieldName

        let label, meaning =
            match field.FieldName with
            | InputTarget.Field field -> FieldTable.labelId field, FieldTable.meaningId field
            | InputTarget.Reason ->
                Vocabulary.messageKey InputTargets.vocabulary InputTarget.Reason "label",
                Vocabulary.messageKey InputTargets.vocabulary InputTarget.Reason "meaning"
            | _ -> invalidOp "An authored scalar needs a semantic presentation."

        { input (prefix @ [ token ]) BindingKind.Text label with
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
                  group.LabelId with
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

        }

    let private target =
        [
            input
                [ "target"; "kind" ]
                (BindingKind.Choice(PrincipalKinds.all))
                (AuthorityOperationDefinitions.labelId "kind")
            { input
                  [ "target"; "issuer" ]
                  BindingKind.Text
                  (AuthorityOperationDefinitions.labelId "issuer") with
                DiagnosticTargets = [ "principal.issuer" ]
            }
            { input
                  [ "target"; "subject" ]
                  BindingKind.Text
                  (AuthorityOperationDefinitions.labelId "subject") with
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
            ]
        | AuthorityInputPrimitive.Boolean ->
            [ input [ "act"; field.Name ] BindingKind.Boolean field.LabelId ]
        | AuthorityInputPrimitive.Scope ->
            [
                input
                    [ "act"; "scope"; "kind" ]
                    (BindingKind.Group [ "INSTALLATION"; "CASE" ])
                    field.LabelId
                { input
                      [ "act"; "scope"; (FieldTable.key BusinessField.CaseReference) ]
                      BindingKind.Text
                      (FieldTable.labelId BusinessField.CaseReference) with
                    Field = Some(InputTarget.Field BusinessField.CaseReference)
                    MeaningId = Some(FieldTable.meaningId BusinessField.CaseReference)
                    DiagnosticTargets = [ (FieldTable.key BusinessField.CaseReference) ]
                    Branches = [ condition [ "act"; "scope"; "kind" ] "CASE" ]
                }
            ]

    let private authorityDefinition
        (command: AuthorityOperationDefinition)
        : InteractionDefinition =
        let kind = AuthorityKinds.token command.Kind

        {
            Scope = OperationScopes.Authority
            Kind = kind
            Inputs = ordered (identity :: target @ List.collect authorityInput command.Inputs)
            Constants = [ text [ "scope" ] OperationScopes.Authority; text [ "act"; "kind" ] kind ]
        }

    let all =
        List.map caseDefinition CommandPresentation.all
        @ List.map authorityDefinition AuthorityOperationDefinitions.all
