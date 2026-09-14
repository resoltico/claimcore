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
                    "Keep the accepted registration facts or replace all seven registration inputs together."
                    [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ]
                    (registrationFields |> List.map current)
                correctionGroup
                    "decision"
                    "Payment decision"
                    "Keep the accepted decision, replace its complete date, amount, and currency tuple, or clear it."
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
                    "Keep the accepted payment record, replace its date, or clear the recorded payment date; clearing does not reverse an external transfer."
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
                "Create the basic record. No decision or payment is inferred."
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
            "Correct existing decided, paid, or closed case facts atomically. Keep reads accepted values; clearing a payment record does not reverse an external transfer."
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
                "Remove the unpaid decision tuple while retaining its earlier history."
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
                "Set the case status to CLOSED. Closing does not imply any payment."
                (fields [])
            definition
                CommandKind.Reopen
                "Reopen the case"
                "Set the case status to OPENED, retaining its other fields and history."
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
