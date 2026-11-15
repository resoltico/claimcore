namespace ClaimCore.Domain.Changes

open ClaimCore.Domain

module PersonDescription =
    let describe (before: ActorAccess option) (after: PersonValue) =
        let priorProfile = before |> Option.bind (fun actor -> actor.Actor.Profile)
        let principal = after.Principal |> Option.orElse (before |> Option.map _.Principal)
        let grants = before |> Option.map _.Grants |> Option.defaultValue [] |> Set.ofList

        {
            PersonPartDescription.Principal =
                Difference.value (before |> Option.map _.Principal) principal
            Enabled = Difference.value (before |> Option.map _.Enabled) (Some after.Enabled)
            DisplayName =
                Difference.value
                    (priorProfile |> Option.map _.DisplayName)
                    (ProfileValue.value after.Profile |> Option.map _.DisplayName)
            Contact =
                Difference.value
                    (priorProfile |> Option.bind _.Contact)
                    (ProfileValue.value after.Profile |> Option.bind _.Contact)
            Grants =
                after.Grants
                |> List.map (fun assignment ->
                    {
                        Grant = assignment.Grant
                        Before = Set.contains assignment.Grant grants
                        After = assignment.Active
                    })
        }
