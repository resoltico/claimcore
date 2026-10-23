namespace ClaimCore.Domain

open ClaimCore.Contract

/// Semantic operation descriptions, not widgets or client-owned transition rules.
type CommandDefinition =
    {
        Kind: CaseActKind
        LabelId: string
        MeaningId: string
        ConfirmationId: string
        Inputs: CommandInputShape
    }

module CommandDefinitions =
    let private registrationFields =
        [
            (InputField.Business BusinessField.IncidentDate)
            (InputField.Business BusinessField.IncidentNotificationDate)
            (InputField.Business BusinessField.IncidentCountry)
            (InputField.Business BusinessField.ClaimantName)
            (InputField.Business BusinessField.InsurerName)
            (InputField.Business BusinessField.ClaimedAmount)
            (InputField.Business BusinessField.ClaimedCurrency)
        ]

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
                | InputField.Business field -> PrefillSource.CurrentField field
                | InputField.Reason -> PrefillSource.Blank
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
                    [
                        current (InputField.Business BusinessField.PaymentDecisionDate)
                        current (InputField.Business BusinessField.PayableAmount)
                        current (InputField.Business BusinessField.PayableCurrency)
                    ]
                correctionGroup
                    CorrectionGroup.Payment
                    "group.payment.label"
                    "group.payment.meaning"
                    [
                        CorrectionGroupAction.Keep
                        CorrectionGroupAction.Replace
                        CorrectionGroupAction.Clear
                    ]
                    [ current (InputField.Business BusinessField.PaymentDate) ]
            ]

    let private definition kind label meaning inputs =
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
                (fields
                    [
                        current (InputField.Business BusinessField.PaymentDecisionDate)
                        current (InputField.Business BusinessField.PayableAmount)
                        current (InputField.Business BusinessField.PayableCurrency)
                    ])
            definition
                (CaseActKind.Business BusinessActKind.WithdrawDecision)
                "command.WITHDRAW_DECISION.label"
                "command.WITHDRAW_DECISION.meaning"
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.RecordPayment)
                "command.RECORD_PAYMENT.label"
                "command.RECORD_PAYMENT.meaning"
                (fields [ blank (InputField.Business BusinessField.PaymentDate) ])
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
                (fields [ blank InputField.Reason ])
            definition
                (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                "command.REINSTATE.label"
                "command.REINSTATE.meaning"
                (fields [ blank InputField.Reason ])
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
