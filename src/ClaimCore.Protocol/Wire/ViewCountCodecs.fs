namespace ClaimCore.Protocol

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

    let cases =
        (record (fun basis items ->
            {
                CaseViews.Basis = basis
                Items = items
            })
        : Fields<CaseViews, _>)
        |> field "basis" _.Basis ReadRowCodecs.dateBasis
        |> field "items" _.Items (list CaseView.all.Length caseCount)
        |> build
        |> named "CaseViews"

    let reminders =
        (record (fun basis items ->
            {
                ReminderViews.Basis = basis
                Items = items
            })
        : Fields<ReminderViews, _>)
        |> field "basis" _.Basis ReadRowCodecs.dateBasis
        |> field "items" _.Items (list ReminderView.all.Length reminderCount)
        |> build
        |> named "ReminderViews"
