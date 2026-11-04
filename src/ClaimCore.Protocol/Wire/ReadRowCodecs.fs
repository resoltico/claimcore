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

    let workView =
        (record
            (fun
                revision
                changedAt
                recordedBy
                summary
                nextAction
                pending
                overdue
                needsNextAction
                canUpdateSummary
                canAssign
                canCreateReminder
                responsible
                followUp
                due
                basis ->
                {
                    CaseWorkView.Revision = revision
                    ChangedAt = changedAt
                    RecordedBy = recordedBy
                    Summary = summary
                    NextAction = nextAction
                    PendingReminders = pending
                    OverdueReminders = overdue
                    NeedsNextAction = needsNextAction
                    CanUpdateSummary = canUpdateSummary
                    CanAssign = canAssign
                    CanCreateReminder = canCreateReminder
                    Responsible = responsible
                    FollowUp = followUp
                    Due = due
                    Basis = basis
                })
        : Fields<CaseWorkView, _>)
        |> field "revision" _.Revision revision
        |> field "changedAt" _.ChangedAt (nullable instant)
        |> field "recordedBy" _.RecordedBy (nullable ActorCodecs.actorRef)
        |> field "summary" _.Summary (nullable handlingSummary)
        |> field "nextAction" _.NextAction (nullable reminderAction)
        |> field "pendingReminders" _.PendingReminders (integer 0L SafeInteger.Maximum)
        |> field "overdueReminders" _.OverdueReminders (integer 0L SafeInteger.Maximum)
        |> field "needsNextAction" _.NeedsNextAction boolean
        |> field "canUpdateSummary" _.CanUpdateSummary boolean
        |> field "canAssign" _.CanAssign boolean
        |> field "canCreateReminder" _.CanCreateReminder boolean
        |> field "responsible" _.Responsible (nullable ActorCodecs.actorRef)
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
        (record (fun id reference revision changedAt state headline attention ->
            {
                CaseSummary.CaseId = id
                Reference = reference
                Revision = revision
                ChangedAt = changedAt
                State = state
                Headline = headline
                Attention = attention
            })
        : Fields<CaseSummary, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "changedAt" _.ChangedAt instant
        |> field "state" _.State (CaseStateCodecs.state "CaseSummaryState" facts)
        |> field "headline" _.Headline (VocabularyCodec.create CaseHeadline.vocabulary)
        |> field
            "attention"
            _.Attention
            (list CaseAttention.all.Length (VocabularyCodec.create CaseAttention.vocabulary))
        |> build
        |> named "CaseSummary"
