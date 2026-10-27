namespace ClaimCore.Domain

module BusinessFields =
    // This exhaustive field projection formats data; it selects no business policy.
    // fsharplint:disable-next-line CyclomaticComplexity
    let value (fields: CaseFields) =
        function
        | BusinessField.CaseReference -> Some(CaseReference.format fields.CaseReference)
        | BusinessField.Status -> Some(CaseStatuses.token fields.Status)
        | BusinessField.IncidentDate -> Some(CalendarDate.format fields.Registration.IncidentDate)
        | BusinessField.IncidentNotificationDate ->
            Some(CalendarDate.format fields.Registration.IncidentNotificationDate)
        | BusinessField.IncidentCountry ->
            Some(CountryCode.format fields.Registration.IncidentCountry)
        | BusinessField.ClaimantName -> Some(PartyName.format fields.Registration.ClaimantName)
        | BusinessField.InsurerName -> Some(PartyName.format fields.Registration.InsurerName)
        | BusinessField.ClaimedAmount -> Some(Money.format fields.Registration.ClaimedAmount)
        | BusinessField.ClaimedCurrency -> Some(Money.currency fields.Registration.ClaimedAmount)
        | BusinessField.PaymentDecisionDate ->
            fields.Decision
            |> Option.map (fun value -> CalendarDate.format value.PaymentDecisionDate)
        | BusinessField.PayableAmount ->
            fields.Decision |> Option.map (fun value -> Money.format value.PayableAmount)
        | BusinessField.PayableCurrency ->
            fields.Decision |> Option.map (fun value -> Money.currency value.PayableAmount)
        | BusinessField.PaymentDate ->
            fields.Payment
            |> Option.map (fun value -> CalendarDate.format value.PaymentDate)

    let values fields =
        Record.fields
        |> List.map (fun definition -> definition.Field, value fields definition.Field)
