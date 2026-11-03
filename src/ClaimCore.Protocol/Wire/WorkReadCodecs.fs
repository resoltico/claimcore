namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module WorkReadCodecs =
    open Codec
    open ResponseScalars

    let reminder =
        (record (fun basis item ->
            {
                ReminderRead.Basis = basis
                Item = item
            })
        : Fields<ReminderRead, _>)
        |> field "basis" _.Basis ReadRowCodecs.dateBasis
        |> field "item" _.Item WorkCodecs.reminderRead
        |> build
        |> named "ReminderRead"

    let reminders =
        (record (fun basis items next ->
            {
                ReminderPage.Basis = basis
                Items = items
                NextCursor = next
            })
        : Fields<ReminderPage, _>)
        |> field "basis" _.Basis ReadRowCodecs.dateBasis
        |> field "items" _.Items (list Paging.MaximumPageSize WorkCodecs.reminderRead)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumCaseCursorCharacters))
        |> build
        |> named "ReminderPage"

    let overview =
        (record
            (fun
                basis
                mine
                actionable
                missing
                overdueCases
                overdueReminders
                unassigned
                unavailable
                attention ->
                {
                    WorkOverview.Basis = basis
                    Mine = mine
                    ActionableCases = actionable
                    MissingPlanCases = missing
                    OverdueCases = overdueCases
                    OverdueReminders = overdueReminders
                    UnassignedCases = unassigned
                    UnavailableAssigneeCases = unavailable
                    AttentionCases = attention
                })
        : Fields<WorkOverview, _>)
        |> field "basis" _.Basis ReadRowCodecs.dateBasis
        |> field "mine" _.Mine boolean
        |> field "actionableCases" _.ActionableCases (integer 0L SafeInteger.Maximum)
        |> field "missingPlanCases" _.MissingPlanCases (integer 0L SafeInteger.Maximum)
        |> field "overdueCases" _.OverdueCases (integer 0L SafeInteger.Maximum)
        |> field "overdueReminders" _.OverdueReminders (integer 0L SafeInteger.Maximum)
        |> field "unassignedCases" _.UnassignedCases (integer 0L SafeInteger.Maximum)
        |> field
            "unavailableAssigneeCases"
            _.UnavailableAssigneeCases
            (integer 0L SafeInteger.Maximum)
        |> field "attentionCases" _.AttentionCases (integer 0L SafeInteger.Maximum)
        |> build
        |> named "WorkOverview"

    let private version =
        record (fun id revision -> id, revision)
        |> field "reminderId" fst reminderId
        |> field "revision" snd positiveRevision
        |> build

    let private subject =
        tagged
            "ActivitySubject"
            [
                variant
                    "CASE"
                    (fun (revision, act) -> ActivitySubject.Case(revision, act))
                    (function
                    | ActivitySubject.Case(revision, act) -> Some(revision, act)
                    | _ -> None)
                    (record (fun revision act -> revision, act)
                     |> field "revision" fst positiveRevision
                     |> field "act" snd (VocabularyCodec.create CaseActKind.vocabulary))
                variant
                    "WORK"
                    (fun (revision, act) -> ActivitySubject.Work(revision, act))
                    (function
                    | ActivitySubject.Work(revision, act) -> Some(revision, act)
                    | _ -> None)
                    (record (fun revision act -> revision, act)
                     |> field "revision" fst positiveRevision
                     |> field "act" snd (VocabularyCodec.create WorkActKind.vocabulary))
                variant
                    "REMINDER"
                    (fun (act, id, revision, next) ->
                        ActivitySubject.Reminder(act, id, revision, next))
                    (function
                    | ActivitySubject.Reminder(act, id, revision, next) ->
                        Some(act, id, revision, next)
                    | _ -> None)
                    (record (fun act id revision next -> act, id, revision, next)
                     |> field
                         "act"
                         (fun (act, _, _, _) -> act)
                         (VocabularyCodec.create ReminderActKind.vocabulary)
                     |> field "reminderId" (fun (_, id, _, _) -> id) reminderId
                     |> field "revision" (fun (_, _, revision, _) -> revision) positiveRevision
                     |> field "next" (fun (_, _, _, next) -> next) (nullable version))
            ]

    let private activity =
        (record (fun operation position subject recordedAt recordedBy detail ->
            {
                CaseActivity.OperationId = operation
                Position = position
                Subject = subject
                RecordedAt = recordedAt
                RecordedBy = recordedBy
                Detail = detail
            })
        : Fields<CaseActivity, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position ResponseCodecs.position
        |> field "subject" _.Subject subject
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy actorId
        |> field "detail" _.Detail (nullable ReceiptCodecs.receipt)
        |> build
        |> named "CaseActivity"

    let activityPage =
        (record (fun parent reference items next ->
            {
                CaseActivityPage.CaseId = parent
                Reference = reference
                Items = items
                NextCursor = next
            })
        : Fields<CaseActivityPage, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "items" _.Items (list Paging.MaximumPageSize activity)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumCaseCursorCharacters))
        |> build
        |> named "CaseActivityPage"
