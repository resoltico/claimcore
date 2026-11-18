namespace ClaimCore.Domain.Changes

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates

module PersonDescription =
    let describe (before: Person option) (after: PersonValue) =
        let priorProfile = before |> Option.bind (fun actor -> actor.Actor.Profile)

        let principal =
            after.Principal |> Option.orElse (before |> Option.map _.Actor.Principal)

        let grants =
            before
            |> Option.map (_.Grants >> Map.keys >> Set.ofSeq)
            |> Option.defaultValue Set.empty

        {
            PersonPartDescription.Profiles =
                {
                    Before = priorProfile
                    After = ProfileValue.value after.Profile
                }
            Principal = Difference.value (before |> Option.map _.Actor.Principal) principal
            Enabled = Difference.value (before |> Option.map _.Actor.Enabled) (Some after.Enabled)
            ProfilePresent =
                Difference.value
                    (before |> Option.map (fun person -> person.Profile <> ProfileValue.Absent))
                    (Some(after.Profile <> ProfileValue.Absent))
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

    let fromChange (before: Person option) (change: PartChange) =
        match change.Aggregate, change.Value with
        | AggregateReference.Person id, PartValue.Person value when
            before |> Option.forall (fun person -> person.Id = id)
            ->
            Ok(describe before value)
        | _ -> Error RecordViolation.InvalidSubjectState

    let changesSignInIdentity (description: PersonPartDescription) =
        description.Principal
        |> Option.exists (fun change -> change.Before.IsSome && change.After.IsSome)
