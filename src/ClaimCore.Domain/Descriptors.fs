namespace ClaimCore.Domain

open ClaimCore.Contract

module ScalarRules =
    let textConstraints rule =
        match rule with
        | ScalarRule.Text constraints -> constraints
        | ScalarRule.Amount amount -> amount.Text
        | ScalarRule.Currency currency -> currency.Text
        | ScalarRule.CalendarDate _
        | ScalarRule.CaseStatus _ -> invalidArg "rule" "The scalar rule is not text-like."
