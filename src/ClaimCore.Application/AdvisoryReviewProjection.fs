namespace ClaimCore.Application

open ClaimCore.Domain

/// Builds advisory review material from an already captured business-time context. Keeping this
/// separate from recovery projection avoids a god module while preserving one shared review shape.
module internal AdvisoryReviewProjection =
    let create
        (context: BusinessContext)
        (before: Claim option)
        (proposed: Claim)
        : AdvisoryReview =
        let previous =
            before
            |> Option.map (
                Claim.view >> fun view -> FieldDefinitions.values view.Fields |> Map.ofList
            )
            |> Option.defaultValue Map.empty

        let next =
            proposed
            |> Claim.view
            |> fun view -> FieldDefinitions.values view.Fields |> Map.ofList

        let changes =
            FieldDefinitions.all
            |> List.choose (fun field ->
                let beforeValue = previous |> Map.tryFind field.Name |> Option.flatten
                let afterValue = next |> Map.tryFind field.Name |> Option.flatten

                if beforeValue = afterValue then
                    None
                else
                    Some
                        {
                            FieldName = field.Name
                            Before = beforeValue
                            After = afterValue
                        })

        {
            Before = before |> Option.map Claim.view
            Proposed = Claim.view proposed
            Changes = changes
            Context = TypedProjection.runtimeContext context
            IsAdvisory = true
        }

    let withClock (clock: IBusinessTime) before proposed =
        create (clock.Capture()) before proposed
