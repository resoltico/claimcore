namespace ClaimCore.Domain.Changes

open ClaimCore.Domain

module WorkDescription =
    let describe (before: CaseWork option) (after: CaseWork option) =
        {
            WorkPartDescription.Values = { Before = before; After = after }
            Responsible =
                Difference.value
                    (before |> Option.bind _.Responsible)
                    (after |> Option.bind _.Responsible)
            Summary =
                Difference.value (before |> Option.bind _.Summary) (after |> Option.bind _.Summary)
        }
