namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Json

module internal ReadRowCodecs =
    open Codec
    open ResponseScalars

    let private parsed parse format codec =
        codec
        |> mapResult
            (fun path value ->
                parse value
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "value outside its wire grammar"
                    }))
            format

    let private money =
        (record Money.create: Fields<Money, _>)
        |> field "amount" Money.amount (parsed Amount.parse Amount.format amount)
        |> field "currency" Money.currencyValue (parsed Currency.parse Currency.format currency)
        |> build
        |> named "SummaryMoney"

    let private parties =
        (record (fun claimant insurer -> claimant, insurer): Fields<PartyName * PartyName, _>)
        |> field "claimantName" fst partyName
        |> field "insurerName" snd partyName

    let private amounts =
        (record (fun claimed payable -> claimed, payable): Fields<Money * Money option, _>)
        |> field "claimed" fst money
        |> field "payable" snd (nullable money)

    let private facts =
        (record (fun status parties amounts progress notified ->
            let claimant, insurer = parties
            let claimed, payable = amounts

            {
                CaseSummaryFacts.Status = status
                ClaimantName = claimant
                InsurerName = insurer
                Claimed = claimed
                Payable = payable
                Progress = progress
                IncidentNotificationDate = notified
            })
        : Fields<CaseSummaryFacts, _>)
        |> field "status" _.Status (VocabularyCodec.create CaseStatus.vocabulary)
        |> flatten (fun value -> value.ClaimantName, value.InsurerName) parties
        |> flatten (fun value -> value.Claimed, value.Payable) amounts
        |> field "progress" _.Progress (VocabularyCodec.create CaseProgress.vocabulary)
        |> field
            "incidentNotificationDate"
            _.IncidentNotificationDate
            (parsed CalendarDate.parse CalendarDate.format date)

    let caseSummary =
        (record (fun reference revision changedAt state ->
            {
                CaseSummary.Reference = reference
                Revision = revision
                ChangedAt = changedAt
                State = state
            })
        : Fields<CaseSummary, _>)
        |> field "caseReference" _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "changedAt" _.ChangedAt instant
        |> field "state" _.State (CaseStateCodecs.state "CaseSummaryState" facts)
        |> build
        |> named "CaseSummary"
