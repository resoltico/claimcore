namespace ClaimCore.Domain

/// Semantic operation descriptions, not widgets or client-owned transition rules.
type CommandDefinition =
    {
        Kind: CaseActKind
        Label: string
        Meaning: string
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
            Label = label
            Meaning = meaning
            Actions = actions
            ReplaceFields = replaceFields
        }

    let private correctionGroups =
        CommandInputShape.CorrectionGroups
            [
                correctionGroup
                    CorrectionGroup.Registration
                    "Registration"
                    "Keep the current registration details or replace all seven details together."
                    [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
                    (registrationFields |> List.map current)
                correctionGroup
                    CorrectionGroup.Decision
                    "Payment decision"
                    "Keep the current payment decision, replace its date, amount and currency together, or clear the decision."
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
                    "Payment record"
                    "Keep the current payment date, replace it, or clear the recorded date. Clearing the date does not refund money."
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
            Label = label
            Meaning = meaning
            Inputs = inputs
        }

    let private registrationDefinitions =
        [
            definition
                (CaseActKind.Business BusinessActKind.Open)
                "Open a case"
                "Register the incident, claimant, insurer and amount claimed."
                (registrationFields |> List.map blank |> fields)
            definition
                (CaseActKind.Business BusinessActKind.AmendRegistration)
                "Amend registration"
                "Replace the initial facts. The handler's case reference remains unchanged."
                (registrationFields |> List.map current |> fields)
        ]

    let private correctionDefinition =
        definition
            (CaseActKind.Business BusinessActKind.CorrectCase)
            "Correct case facts"
            "Correct recorded details on a case with a decision, payment or closed status. All changes are recorded together. Clearing a payment date does not refund money."
            correctionGroups

    let private paymentAndStatusDefinitions =
        [
            definition
                (CaseActKind.Business BusinessActKind.Decide)
                "Record payment decision"
                "Record or replace the decided amount and currency. This does not send money."
                (fields
                    [
                        current (InputField.Business BusinessField.PaymentDecisionDate)
                        current (InputField.Business BusinessField.PayableAmount)
                        current (InputField.Business BusinessField.PayableCurrency)
                    ])
            definition
                (CaseActKind.Business BusinessActKind.WithdrawDecision)
                "Withdraw payment decision"
                "Clear the unpaid decision while preserving its history."
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.RecordPayment)
                "Record actual payment"
                "Record the date the full decided amount was actually paid. This does not execute or verify a transfer."
                (fields [ blank (InputField.Business BusinessField.PaymentDate) ])
            definition
                (CaseActKind.Business BusinessActKind.ClearPayment)
                "Correct an erroneous payment record"
                "Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains."
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.Close)
                "Close the case"
                "Close the case. Closing does not mean that payment was made."
                (fields [])
            definition
                (CaseActKind.Business BusinessActKind.Reopen)
                "Reopen the case"
                "Reopen the case, preserving its details and history."
                (fields [])
        ]

    let private stewardshipDefinitions =
        [
            definition
                (CaseActKind.Stewardship StewardshipActKind.Void)
                "Void case record"
                "Block business commands while retaining the case and its history."
                (fields [ blank InputField.Reason ])
            definition
                (CaseActKind.Stewardship StewardshipActKind.Reinstate)
                "Reinstate case record"
                "Restore ordinary case work on a voided record."
                (fields [ blank InputField.Reason ])
            definition
                (CaseActKind.Stewardship StewardshipActKind.Erase)
                "Erase case payload"
                "Permanently remove business payload from the live database. WAL, backups and copies elsewhere are not erased."
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
