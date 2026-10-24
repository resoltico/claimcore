namespace ClaimCore.Domain

open ClaimCore.Contract

module CommandDefinitions =
    let private groupFields group =
        FieldTable.rows
        |> List.filter (fun row -> row.Group = group)
        |> List.map (fun row -> InputTarget.Field row.Field)

    let private registrationFields = groupFields FieldGroup.Registration

    let private blank fieldName : FieldInputDefinition =
        {
            FieldName = fieldName
            Prefill = PrefillSource.Blank
        }

    let private current fieldName : FieldInputDefinition =
        {
            FieldName = fieldName
            Prefill =
                match fieldName with
                | InputTarget.Field field -> PrefillSource.CurrentField field
                | InputTarget.Reason -> PrefillSource.Blank
                | _ -> invalidArg "fieldName" "Expected a business or stewardship scalar."
        }

    let private fields values = CommandInputShape.Fields values

    let private correctionGroup name label meaning actions replaceFields =
        {
            Name = name
            LabelId = label
            MeaningId = meaning
            Actions = actions
            ReplaceFields = replaceFields
        }

    let private correctionGroups =
        CommandInputShape.CorrectionGroups
            [
                correctionGroup
                    CorrectionGroup.Registration
                    "group.registration.label"
                    "group.registration.meaning"
                    [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
                    (registrationFields |> List.map current)
                correctionGroup
                    CorrectionGroup.Decision
                    "group.decision.label"
                    "group.decision.meaning"
                    [
                        CorrectionGroupAction.Keep
                        CorrectionGroupAction.Replace
                        CorrectionGroupAction.Clear
                    ]
                    (groupFields FieldGroup.Decision |> List.map current)
                correctionGroup
                    CorrectionGroup.Payment
                    "group.payment.label"
                    "group.payment.meaning"
                    [
                        CorrectionGroupAction.Keep
                        CorrectionGroupAction.Replace
                        CorrectionGroupAction.Clear
                    ]
                    (groupFields FieldGroup.Payment |> List.map current)
            ]

    let private definition kind label meaning inputs : CommandDefinition =
        {
            Kind = kind
            LabelId = label
            MeaningId = meaning
            ConfirmationId = "ui.reviewConfirmation"
            Inputs = inputs
        }

    let private registrationDefinitions =
        [
            definition
                (CaseActKind.Business BusinessActKind.Open)
                "command.OPEN.label"
                "command.OPEN.meaning"
                (registrationFields |> List.map blank |> fields)
            definition
                (CaseActKind.Business BusinessActKind.AmendRegistration)
                "command.AMEND_REGISTRATION.label"
                "command.AMEND_REGISTRATION.meaning"
                (registrationFields |> List.map current |> fields)
        ]

    let private correctionDefinition =
        definition
            (CaseActKind.Business BusinessActKind.CorrectCase)
            "command.CORRECT_CASE.label"
            "command.CORRECT_CASE.meaning"
            correctionGroups

    let private paymentAndStatusDefinitions =
        [
            definition
                (CaseActKind.Business BusinessActKind.Decide)
                "command.DECIDE.label"
                "command.DECIDE.meaning"
                (fields (groupFields FieldGroup.Decision |> List.map current))
            definition
                (CaseActKind.Business BusinessActKind.WithdrawDecision)
                "command.WITHDRAW_DECISION.label"
                "command.WITHDRAW_DECISION.meaning"
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.RecordPayment)
                "command.RECORD_PAYMENT.label"
                "command.RECORD_PAYMENT.meaning"
                (groupFields FieldGroup.Payment |> List.map blank |> fields)
            definition
                (CaseActKind.Business BusinessActKind.ClearPayment)
                "command.CLEAR_PAYMENT.label"
                "command.CLEAR_PAYMENT.meaning"
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.Close)
                "command.CLOSE.label"
                "command.CLOSE.meaning"
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.Reopen)
                "command.REOPEN.label"
                "command.REOPEN.meaning"
                (fields [])
        ]

    let private stewardshipDefinitions =
        [
            definition
                (CaseActKind.Stewardship StewardshipActKind.Void)
                "command.VOID.label"
                "command.VOID.meaning"
                (fields [ blank InputTarget.Reason ])
            definition
                (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                "command.REINSTATE.label"
                "command.REINSTATE.meaning"
                (fields [ blank InputTarget.Reason ])
            definition
                (CaseActKind.Stewardship StewardshipActKind.Erase)
                "command.ERASE.label"
                "command.ERASE.meaning"
                (fields [])
        ]

    let all =
        registrationDefinitions
        @ [ correctionDefinition ]
        @ paymentAndStatusDefinitions
        @ stewardshipDefinitions

    let forKind kind =
        all |> List.find (fun definition -> definition.Kind = kind)

    /// Flat scalar adapters may bind only commands with a scalar object shape. Grouped correction
    /// input is decoded explicitly by its generated transport projection.
    let flatInputFields kind =
        match (forKind kind).Inputs with
        | CommandInputShape.Fields inputs -> Ok inputs
        | CommandInputShape.CorrectionGroups _ ->
            Error "The command requires grouped correction input."
