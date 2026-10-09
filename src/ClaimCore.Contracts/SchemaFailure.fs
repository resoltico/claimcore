namespace ClaimCore.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal SchemaProblem =
    | ObjectRequired
    | StringRequired
    | BooleanRequired
    | NumberRequired
    | ArrayRequired
    | DuplicateMember
    | UnknownMember
    | MissingMember
    | IntegerRange
    | InvalidValue
    | InvalidFormat of string
    | ScalarViolation of InputViolation

/// Opaque, bounded admission cause. No authored values or unknown keys are retained.
type SchemaFailure =
    internal
        {
            DeclaredPath: string list
            Problem: SchemaProblem
        }

module internal SchemaFailures =
    let fail path problem =
        Error
            {
                DeclaredPath = path
                Problem = problem
            }

    let require path problem accepted =
        if accepted then Ok() else fail path problem

    let stringConstraint (rule: ScalarRule option) short long =
        match rule, short, long with
        | Some(ScalarRule.CalendarDate _), _, _ ->
            Some(InputViolation.Date DateViolation.CalendarDateRequired)
        | Some _, Some value, _ -> Some(InputViolation.Text(TextViolation.TooShort value))
        | Some _, _, Some value -> Some(InputViolation.Text(TextViolation.TooLong value))
        | Some(ScalarRule.Amount value), _, _ ->
            Some(
                InputViolation.Amount(
                    AmountViolation.DecimalFormat(
                        value.MaximumIntegerDigits,
                        value.MaximumFractionalDigits
                    )
                )
            )
        | Some(ScalarRule.Currency _), _, _ ->
            Some(InputViolation.Amount AmountViolation.CurrencyFormat)
        | _ -> None
