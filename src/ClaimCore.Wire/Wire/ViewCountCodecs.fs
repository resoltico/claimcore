namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module ViewCountCodecs =
    open Codec

    let private caseCount =
        (record (fun view count ->
            {
                CaseViewCount.View = view
                Count = count
            })
        : Fields<CaseViewCount, _>)
        |> field "view" _.View (VocabularyCodec.create CaseView.vocabulary)
        |> field "count" _.Count (integer 0L SafeInteger.Maximum)
        |> build
        |> named "CaseViewCount"

    let private reminderCount =
        (record (fun view count ->
            {
                ReminderViewCount.View = view
                Count = count
            })
        : Fields<ReminderViewCount, _>)
        |> field "view" _.View (VocabularyCodec.create ReminderView.vocabulary)
        |> field "count" _.Count (integer 0L SafeInteger.Maximum)
        |> build
        |> named "ReminderViewCount"

    let work =
        (record (fun basis cases reminders ->
            {
                WorkViews.Basis = basis
                CaseViews = cases
                ReminderViews = reminders
            })
        : Fields<WorkViews, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "caseViews" _.CaseViews (list CaseView.all.Length caseCount)
        |> field "reminderViews" _.ReminderViews (list ReminderView.all.Length reminderCount)
        |> build
        |> named "WorkViews"
