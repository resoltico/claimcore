namespace ClaimCore.Domain

/// Request parsing adapts independent value failures to their typed input identities.
module internal Validation =
    let invalid field violation =
        Error(Refusal.invalidInput (field, violation))

    let private at target =
        Result.mapError (fun violation -> Refusal.invalidInput (target, violation))

    let onOrBefore field (first: CalendarDate) (last: CalendarDate) =
        if first <= last then
            Ok()
        else
            invalid field Violation.ChronologicalOrder

    let private group result =
        result
        |> Result.mapError (fun issues ->
            issues
            |> List.map (fun (field, violation) ->
                {
                    Target = InputTarget.Field field
                    Violation = violation
                })
            |> InputIssues.ofList
            |> Option.get
            |> Refusal.InvalidInput)

    let registration input = Registration.parse input |> group
    let decision input = Decision.parse input |> group
    let payment input = Payment.parse input |> group
