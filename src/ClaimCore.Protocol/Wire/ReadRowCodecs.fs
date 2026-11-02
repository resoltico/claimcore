namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
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

    let dateBasis =
        (record (fun observed date zone ->
            {
                WorkDateBasis.ObservedAt = observed
                BusinessDate = date
                TimeZoneId = zone
            })
        : Fields<WorkDateBasis, _>)
        |> field "observedAt" _.ObservedAt instant
        |> field
            "businessDate"
            _.BusinessDate
            (calendarDate |> map CalendarDate.value CalendarDate.ofDate)
        |> field "timeZone" _.TimeZoneId nonempty
        |> build
        |> named "WorkDateBasis"

    let accountSubject =
        (record (fun actor subject ->
            {
                AccountSubject.ActorId = actor
                Subject = subject
            })
        : Fields<AccountSubject, _>)
        |> field "actorId" _.ActorId actorId
        |> field "subject" _.Subject nonempty
        |> build
        |> named "AccountSubject"

    let private assignedAccount =
        (record (fun account available ->
            {
                AssignedAccount.Account = account
                Available = available
            })
        : Fields<AssignedAccount, _>)
        |> field "account" _.Account accountSubject
        |> field "available" _.Available boolean
        |> build
        |> named "AssignedAccount"

    let workView =
        (record (fun responsible followUp due basis ->
            {
                CaseWorkView.Responsible = responsible
                FollowUp = followUp
                Due = due
                Basis = basis
            })
        : Fields<CaseWorkView, _>)
        |> field "responsible" _.Responsible (nullable assignedAccount)
        |> field "followUpDate" _.FollowUp (nullable calendarDate)
        |> field "due" _.Due (VocabularyCodec.create CaseDue.vocabulary)
        |> field "basis" _.Basis dateBasis
        |> build
        |> named "CaseWorkView"

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
        (record (fun status parties amounts progress notified work ->
            let claimant, insurer = parties
            let claimed, payable = amounts

            {
                CaseSummaryFacts.Work = work
                Status = status
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
        |> field "work" _.Work workView

    let caseSummary =
        (record (fun id reference revision changedAt state ->
            {
                CaseSummary.CaseId = id
                Reference = reference
                Revision = revision
                ChangedAt = changedAt
                State = state
            })
        : Fields<CaseSummary, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "changedAt" _.ChangedAt instant
        |> field "state" _.State (CaseStateCodecs.state "CaseSummaryState" facts)
        |> build
        |> named "CaseSummary"
