namespace ClaimCore.Wire

open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts

module PaymentPageCodecs =
    open Codec

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
            |> field "position" _.Position LedgerCodecs.effectPosition
            |> field "canRemove" _.CanRemove boolean
            |> build
            |> named "CasePaymentItem"

        let state =
            CaseStateCodecs.state
                "PaymentFinancialState"
                (record id |> field "finance" id ResponseCodecs.financialPosition)

        (record (fun basis id reference revision workRevision state items cursor ->
            {
                CasePaymentPage.Basis = basis
                CaseId = id
                Reference = reference
                Revision = revision
                WorkRevision = workRevision
                State = state
                Items = items
                NextCursor = cursor
            })
        : Fields<CasePaymentPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "caseReference" _.Reference ResponseScalars.caseReference
        |> field "revision" _.Revision ResponseScalars.positiveRevision
        |> field "workRevision" _.WorkRevision ResponseScalars.revision
        |> field "state" _.State state
        |> field "items" _.Items (list PaymentPaging.MaximumLimit item)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "CasePaymentPage"
