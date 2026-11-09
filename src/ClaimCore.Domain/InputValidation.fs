namespace ClaimCore.Domain

/// Request parsing adapts independent value failures to their typed input identities.
module internal InputValidation =
    let invalid field violation =
        Error(Refusal.invalidInput (field, violation))

    let private at target =
        Result.mapError (fun violation -> Refusal.invalidInput (target, violation))

    let onOrBefore field (first: CalendarDate) (last: CalendarDate) =
        if first <= last then
            Ok()
        else
            invalid field Violation.ChronologicalOrder

    let group result =
        result
        |> Result.mapError (fun issues ->
            issues
            |> List.map (fun (field, violation) ->
                {
                    Target = InputTarget.Field field
                    GrantRow = None
                    RelatedDate = None
                    Violation = violation
                })
            |> InputIssues.ofList
            |> Option.get
            |> Refusal.InvalidInput)

    let registration input = Registration.parse input |> group
    let decision input = Decision.parse input |> group
    let payment input = Payment.parse input |> group

    let registrationUpdate input =
        RegistrationUpdateInput.parse input |> group

    let decisionUpdate input =
        DecisionUpdateInput.parse input |> group

    let work input = CaseWorkInput.parse input |> group

    let chronology target counterpart date =
        Error(
            Refusal.InvalidInput(
                InputIssues.ofList
                    [
                        {
                            Target = InputTarget.Field target
                            Violation = Violation.ChronologicalOrder
                            GrantRow = None
                            RelatedDate = Some { Field = counterpart; Value = date }
                        }
                    ]
                |> Option.get
            )
        )

    let registrationMerge result =
        result
        |> Result.mapError (fun (target, counterpart, date) ->
            match chronology target counterpart date with
            | Error refusal -> refusal
            | Ok() -> invalidOp "A chronology failure must refuse.")

    /// One issue per failed rule; prefer the chronology diagnostic carrying its counterpart.
    let ordered result =
        result
        |> Result.mapError (function
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.groupBy (fun issue -> issue.Target, issue.GrantRow, issue.Violation)
                |> List.collect (fun (_, same) ->
                    let related = same |> List.filter (fun issue -> issue.RelatedDate.IsSome)

                    if List.isEmpty related then
                        [ List.head same ]
                    else
                        List.distinct related)
                |> List.sortBy (fun issue -> InputTarget.order issue.Target)
                |> InputIssues.ofList
                |> Option.get
                |> Refusal.InvalidInput
            | refusal -> refusal)
