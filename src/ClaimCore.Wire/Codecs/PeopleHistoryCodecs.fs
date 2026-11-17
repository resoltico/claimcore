namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module PeopleHistoryCodecs =
    open Codec
    open ResponseScalars

    let entry =
        (record
            (fun operation position hash instant executor target task changes available references ->
                {
                    PeopleHistoryEntry.OperationId = operation
                    Position = position
                    OperationHash = hash
                    RecordedAt = instant
                    RecordedBy = executor
                    Target = target
                    Task = task
                    Changes = changes
                    PrivateAvailable = available
                    References = references
                })
        : Fields<PeopleHistoryEntry, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position LedgerCodecs.effectPosition
        |> field "operationHash" _.OperationHash LedgerCodecs.hash
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "target" _.Target ActorCodecs.actorRef
        |> field "task" _.Task (TaskVocabulary.forSubject ActSubject.People)
        |> field "changes" _.Changes PartDescriptionCodecs.person
        |> field "privateAvailable" _.PrivateAvailable boolean
        |> field "references" _.References PartDescriptionCodecs.references
        |> build
        |> named "PeopleHistoryEntry"

    let page =
        (record (fun basis entries next ->
            {
                PeopleHistoryPage.Basis = basis
                Entries = entries
                NextCursor = next
            })
        : Fields<PeopleHistoryPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "entries" _.Entries (list Paging.MaximumPageSize entry)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "PeopleHistoryPage"
