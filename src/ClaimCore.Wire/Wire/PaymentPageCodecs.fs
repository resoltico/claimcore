namespace ClaimCore.Wire

open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts

module PaymentPageCodecs =
    open Codec

    let private position =
        (record (fun sequence index -> EffectPosition.create sequence index |> Option.get)
        : Fields<EffectPosition, _>)
        |> field "sequence" EffectPosition.sequence ResponseScalars.sequence
        |> field
            "effectIndex"
            EffectPosition.index
            (integer 0L (int64 System.Int32.MaxValue) |> map int int64)
        |> build
        |> named "EffectPosition"

    let page =
        let item =
            (record (fun entry position remove ->
                {
                    CasePaymentItem.Entry = entry
                    Position = position
                    CanRemove = remove
                })
            : Fields<CasePaymentItem, _>)
            |> field "entry" _.Entry PaymentCodecs.entry
            |> field "position" _.Position position
            |> field "canRemove" _.CanRemove boolean
            |> build
            |> named "CasePaymentItem"

        let state =
            CaseStateCodecs.state
                "PaymentFinancialState"
                (record id |> field "finance" id ResponseCodecs.financialPosition)

        (record (fun basis id reference revision state items cursor ->
            {
                CasePaymentPage.Basis = basis
                CaseId = id
                Reference = reference
                Revision = revision
                State = state
                Items = items
                NextCursor = cursor
            })
        : Fields<CasePaymentPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> field "state" _.State state
        |> field "items" _.Items (list PaymentPaging.MaximumLimit item)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "CasePaymentPage"
