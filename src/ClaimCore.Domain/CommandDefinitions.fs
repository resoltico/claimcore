namespace ClaimCore.Domain

/// Semantic operation descriptions, not widgets or client-owned transition rules.
type CommandDefinition =
    {
        Kind: CommandKind
        Label: string
        Meaning: string
        Inputs: CommandInputShape
    }

module CommandDefinitions =
    let private registrationFields =
        [
            "incidentDate"
            "incidentNotificationDate"
            "incidentCountry"
            "claimantName"
            "insurerName"
            "claimedAmount"
            "claimedCurrency"
        ]

    let private blank fieldName : FieldInputDefinition =
        {
            FieldName = fieldName
            Prefill = PrefillSource.Blank
        }

    let private current fieldName : FieldInputDefinition =
        {
            FieldName = fieldName
            Prefill = PrefillSource.CurrentField fieldName
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
                    "registration"
                    "Registration"
                    "Keep the current registration details or replace all seven details together."
                    [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
                    (registrationFields |> List.map current)
                correctionGroup
                    "decision"
                    "Payment decision"
                    "Keep the current payment decision, replace its date, amount and currency together, or clear the decision."
                    [
                        CorrectionGroupAction.Keep
                        CorrectionGroupAction.Replace
                        CorrectionGroupAction.Clear
                    ]
                    [
                        current "paymentDecisionDate"
                        current "payableAmount"
                        current "payableCurrency"
                    ]
                correctionGroup
                    "payment"
                    "Payment record"
                    "Keep the current payment date, replace it, or clear the recorded date. Clearing the date does not refund money."
                    [
                        CorrectionGroupAction.Keep
                        CorrectionGroupAction.Replace
                        CorrectionGroupAction.Clear
                    ]
                    [ current "paymentDate" ]
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
                CommandKind.Open
                "Open a case"
                "Register the incident, claimant, insurer and amount claimed."
                (registrationFields |> List.map blank |> fields)
            definition
                CommandKind.AmendRegistration
                "Amend registration"
                "Replace the initial facts. The handler's case reference remains unchanged."
                (registrationFields |> List.map current |> fields)
        ]

    let private correctionDefinition =
        definition
            CommandKind.CorrectCase
            "Correct case facts"
            "Correct recorded details on a case with a decision, payment or closed status. All changes are recorded together. Clearing a payment date does not refund money."
            correctionGroups

    let private paymentAndStatusDefinitions =
        [
            definition
                CommandKind.Decide
                "Record payment decision"
                "Record or replace the decided amount and currency. This does not send money."
                (fields
                    [
                        current "paymentDecisionDate"
                        current "payableAmount"
                        current "payableCurrency"
                    ])
            definition
                CommandKind.WithdrawDecision
                "Withdraw payment decision"
                "Clear the unpaid decision while preserving its history."
                (fields [])
            definition
                CommandKind.RecordPayment
                "Record actual payment"
                "Record the date the full decided amount was actually paid. This does not execute or verify a transfer."
                (fields [ blank "paymentDate" ])
            definition
                CommandKind.ClearPayment
                "Correct an erroneous payment record"
                "Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains."
                (fields [])
            definition
                CommandKind.Close
                "Close the case"
                "Close the case. Closing does not mean that payment was made."
                (fields [])
            definition
                CommandKind.Reopen
                "Reopen the case"
                "Reopen the case, preserving its details and history."
                (fields [])
        ]

    let all =
        registrationDefinitions @ [ correctionDefinition ] @ paymentAndStatusDefinitions

    let forKind kind =
        all |> List.find (fun definition -> definition.Kind = kind)

    /// Flat scalar adapters may bind only commands with a scalar object shape. Grouped correction
    /// input is decoded explicitly by its generated transport projection.
    let flatInputFields kind =
        match (forKind kind).Inputs with
        | CommandInputShape.Fields inputs -> Ok inputs
        | CommandInputShape.CorrectionGroups _ ->
            Error "The command requires grouped correction input."
