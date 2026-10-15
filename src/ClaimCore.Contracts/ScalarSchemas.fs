namespace ClaimCore.Contracts

open ClaimCore.Domain

/// Wire validation projects permanent format only. Admission policy is a descriptor hint.
module internal ScalarSchemas =
    let revision = Schema.integer (Some 0L) (Some SafeInteger.Maximum)
    let positive = Schema.integer (Some 1L) (Some SafeInteger.Maximum)

    let uuid =
        Schema.string
            (Some "uuid")
            (Some
                "^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\\s\\S])")
            (Some 36)
            (Some 36)

    let private pattern grammar =
        Some("^(?:" + grammar + ")(?![\\s\\S])")

    let private text (rule: ScalarTextConstraints) =
        Schema.string
            None
            (rule.Format.Grammar |> Option.bind pattern)
            (Some rule.Format.MinimumCharacters)
            None

    let scalar rule =
        let schema =
            match rule with
            | ScalarRule.Text value -> text value
            | ScalarRule.Amount value -> text value.Text
            | ScalarRule.Currency value -> text value.Text
            | ScalarRule.CalendarDate _ ->
                Schema.string
                    (Some "date")
                    (pattern "(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}")
                    (Some 10)
                    (Some 10)
            | ScalarRule.CaseStatus value ->
                value.AllowedValues
                |> List.map (CaseStatuses.token >> TextConstant)
                |> Schema.enumeration

        Schema.scalarDiagnostics rule [] schema
