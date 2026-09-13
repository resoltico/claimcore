namespace ClaimCore.Domain

/// Semantic operation descriptions, not widgets or client-owned transition rules.
type CommandDefinition =
    {
        Kind: CommandKind
        Label: string
        Meaning: string
        Inputs: CommandInputDefinition list
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

    let private blank fieldName =
        {
            FieldName = fieldName
            Prefill = PrefillSource.Blank
        }

    let private current fieldName =
        {
            FieldName = fieldName
            Prefill = PrefillSource.CurrentField fieldName
        }

    let private definition kind label meaning inputs =
        {
            Kind = kind
            Label = label
            Meaning = meaning
            Inputs = inputs
        }

    let all =
        [
            definition
                CommandKind.Open
                "Open a case"
                "Create the basic record. No decision or payment is inferred."
                (registrationFields |> List.map blank)
            definition
                CommandKind.AmendRegistration
                "Amend registration"
                "Replace the initial facts. The handler's case reference remains unchanged."
                (registrationFields |> List.map current)
            definition
                CommandKind.Decide
                "Record payment decision"
                "Record or replace the decided amount and currency. This does not send money."
                [
                    current "paymentDecisionDate"
                    current "payableAmount"
                    current "payableCurrency"
                ]
            definition
                CommandKind.WithdrawDecision
                "Withdraw payment decision"
                "Remove the unpaid decision tuple while retaining its earlier history."
                []
            definition
                CommandKind.RecordPayment
                "Record actual payment"
                "Record the date the full decided amount was actually paid. This does not execute or verify a transfer."
                [ blank "paymentDate" ]
            definition
                CommandKind.ClearPayment
                "Correct an erroneous payment record"
                "Remove an erroneously recorded payment date. This is not a refund or reversal of an actual transfer. Earlier history remains."
                []
            definition
                CommandKind.Close
                "Close the case"
                "Set the case status to CLOSED. Closing does not imply any payment."
                []
            definition
                CommandKind.Reopen
                "Reopen the case"
                "Set the case status to OPENED, retaining its other fields and history."
                []
        ]

    let forKind kind =
        all |> List.find (fun definition -> definition.Kind = kind)

    /// Ordered field names derive from the semantic input descriptors, never a second inventory.
    let inputFields kind =
        (forKind kind).Inputs |> List.map (fun input -> input.FieldName)
