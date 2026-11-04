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
        |> field "basis" _.Basis workDateBasis
        |> build
        |> named "CaseWorkView"

    let private facts =
        (record (fun fields -> { CaseSummaryFacts.Fields = fields }): Fields<CaseSummaryFacts, _>)
        |> field "fields" _.Fields (ListedCaseFieldCodecs.create CaseFields.declarations)

    let private caseNextAction =
        (record (fun id action due dueState ->
            {
                CaseNextAction.ReminderId = id
                Action = action
                Due = due
                DueState = dueState
            })
        : Fields<CaseNextAction, _>)
        |> field "reminderId" _.ReminderId reminderId
        |> field "action" _.Action reminderAction
        |> field "due" _.Due calendarDate
        |> field
            "dueState"
            _.DueState
            (VocabularyCodec.select ((<>) CaseDue.Unscheduled) CaseDue.vocabulary)
        |> build
        |> named "CaseNextAction"

    let caseSummary =
        (record
            (fun
                id
                reference
                revision
                changedAt
                state
                headline
                attention
                next
                responsible
                pending
                stages ->
                {
                    CaseSummary.CaseId = id
                    Reference = reference
                    Revision = revision
                    ChangedAt = changedAt
                    State = state
                    Headline = headline
                    Attention = attention
                    NextAction = next
                    Responsible = responsible
                    PendingReminders = pending
                    Stages = stages
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
        |> field "nextAction" _.NextAction (nullable caseNextAction)
        |> field "responsible" _.Responsible (nullable ActorCodecs.actorRef)
        |> field "pendingReminders" _.PendingReminders (integer 0L SafeInteger.Maximum)
        |> field "stages" _.Stages (nullable CaseStageCodecs.outline)
        |> build
        |> named "CaseSummary"
