namespace ClaimCore.Domain.Changes

module ReminderDescription =
    let describe (before: ReminderValue option) (after: ReminderValue) =
        let prior = before |> Option.bind _.Plan

        {
            ReminderPartDescription.Plans = { Before = prior; After = after.Plan }
            Action =
                Difference.value (prior |> Option.map _.Action) (after.Plan |> Option.map _.Action)
            Due = Difference.value (prior |> Option.map _.Due) (after.Plan |> Option.map _.Due)
            State = Difference.value (before |> Option.map _.State) (Some after.State)
        }
