namespace ClaimCore.Domain.Changes

module PaymentDescription =
    let describe (before: PaymentValue option) (after: PaymentValue) totals =
        {
            PaymentPartDescription.Totals = totals
            Entry = after.Entry |> Option.orElse (before |> Option.bind _.Entry)
            Removed = Difference.value (before |> Option.map _.Removed) (Some after.Removed)
        }
