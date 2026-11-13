namespace ClaimCore.Domain

/// Private payment content becomes unavailable after parent erasure; accepting provenance remains.
[<RequireQualifiedAccess>]
type PaymentProjectionContent =
    | Available of PaymentEntry * removed: bool
    | Cleared of removed: bool

type PaymentProjectionRow =
    {
        CaseId: CaseId
        PaymentId: OperationId
        Position: EffectPosition
        Content: PaymentProjectionContent
    }

module internal CasePayments =
    let active (payments: Map<OperationId, PaymentProjectionRow>) =
        payments
        |> Map.toList
        |> List.choose (fun (id, row) ->
            match row.Content with
            | PaymentProjectionContent.Available(entry, false) -> Some(id, entry)
            | _ -> None)
        |> Map.ofList

    let dates entries =
        match entries |> Map.values |> Seq.map PaymentEntry.date |> Seq.toList with
        | [] -> None
        | dates -> PaymentDateBounds.create (List.min dates) (List.max dates)
