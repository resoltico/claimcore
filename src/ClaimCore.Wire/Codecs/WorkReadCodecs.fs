namespace ClaimCore.Wire

open ClaimCore.Presentation

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
        |> field "basis" _.Basis ResponseScalars.workDateBasis
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
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "items" _.Items (list Paging.MaximumPageSize WorkCodecs.reminderRead)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumCaseCursorCharacters))
        |> build
        |> named "ReminderPage"

    let notes =
        (record (fun basis parent reference notes cursor ->
            {
                CaseNotesPage.Basis = basis
                CaseId = parent
                Reference = reference
                Notes = notes
                NextCursor = cursor
            })
        : Fields<CaseNotesPage, _>)
        |> field "basis" _.Basis workDateBasis
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
        |> field "notes" _.Notes WorkCodecs.notes
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "CaseNotesPage"
