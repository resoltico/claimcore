namespace ClaimCore.Application.Storage

open System
open ClaimCore.Application.Contracts
open ClaimCore.Domain

module internal DisclosureStorage =
    let private kind = Vocabulary.token DisclosureKind.vocabulary

    let private parameter project value =
        value |> Option.map project |> DatabaseSession.parameter

    let append connection (event: DisclosureEvent) token =
        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.disclosure_trail(actor_id,kind,case_id,attachment_id,outcome,export_query,export_filtered,export_paged,export_order,export_grouping,export_row_count,disclosed_count,disclosed_cases,disclosed_people) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13::uuid[],$14::uuid[])"
            [|
                box (ActorId.value event.ActorId)
                box (kind event.Kind)
                parameter CaseId.value event.CaseId
                parameter AttachmentId.value event.AttachmentId
                box (Vocabulary.token DisclosureOutcome.vocabulary event.Outcome)
                parameter (fun value -> DisclosureExportQuery.token value.Query) event.Export
                parameter (fun (value: DisclosureExport) -> value.Selection.Filtered) event.Export
                parameter (fun (value: DisclosureExport) -> value.Selection.Paged) event.Export
                parameter
                    (Vocabulary.token CaseListOrder.vocabulary)
                    (event.Export
                     |> Option.bind (fun (value: DisclosureExport) -> value.Selection.Order))
                parameter
                    (Vocabulary.token CaseGrouping.vocabulary)
                    (event.Export
                     |> Option.bind (fun (value: DisclosureExport) -> value.Selection.Grouping))
                parameter (fun (export: DisclosureExport) -> export.RowCount) event.Export
                parameter (fun (page: DisclosurePageEvidence) -> page.RowCount) event.Page
                parameter
                    (fun (page: DisclosurePageEvidence) ->
                        page.Cases |> List.map CaseId.value |> List.toArray)
                    event.Page
                parameter
                    (fun (page: DisclosurePageEvidence) ->
                        page.People |> List.map ActorId.value |> List.toArray)
                    event.Page
            |]
            token

    let prune connection observedAt token =
        DatabaseSession.execute
            connection
            "SELECT claimcore.prune_disclosure_trail($1,$2)"
            [|
                box (DisclosureTrail.cutoff observedAt)
                box DisclosureTrail.retentionMonths
            |]
            token

    let private corrupt () =
        raise (RecordCorruptException "Invalid stored disclosure event.")

    let private event row =
        let optional vocabulary ordinal =
            DatabaseSession.optional<string> ordinal row
            |> Option.map (DatabaseSession.admitted (Vocabulary.tryParse vocabulary))

        let order = optional CaseListOrder.vocabulary 10
        let grouping = optional CaseGrouping.vocabulary 11

        let export =
            match
                optional DisclosureExportQuery.vocabulary 7,
                DatabaseSession.optional<bool> 8 row,
                DatabaseSession.optional<bool> 9 row,
                DatabaseSession.optional<int> 12 row
            with
            | None, None, None, None when order.IsNone && grouping.IsNone -> None
            | Some query, Some filtered, Some paged, Some count when count >= 0 ->
                Some
                    {
                        Query = query
                        Selection =
                            {
                                Filtered = filtered
                                Paged = paged
                                Order = order
                                Grouping = grouping
                            }
                        RowCount = count
                    }
            | _ -> corrupt ()

        let page =
            match
                DatabaseSession.optional<int> 13 row,
                DatabaseSession.optional<Guid array> 14 row,
                DatabaseSession.optional<Guid array> 15 row
            with
            | None, None, None -> None
            | Some count, Some cases, Some people ->
                Some(
                    DisclosurePageEvidence.create
                        count
                        (cases |> Array.map (DatabaseSession.admitted CaseId.create) |> Array.toList)
                        (people
                         |> Array.map (DatabaseSession.admitted ActorId.create)
                         |> Array.toList)
                )
            | _ -> corrupt ()

        {
            RecordedDisclosure.Id = DatabaseSession.value<int64> 0 row
            RecordedAt = DatabaseSession.instant 1 row
            Event =
                {
                    ActorId =
                        DatabaseSession.value<Guid> 2 row |> DatabaseSession.admitted ActorId.create
                    Kind =
                        DatabaseSession.value<string> 3 row
                        |> DatabaseSession.admitted (Vocabulary.tryParse DisclosureKind.vocabulary)
                    CaseId =
                        DatabaseSession.optional<Guid> 4 row
                        |> Option.map (DatabaseSession.admitted CaseId.create)
                    AttachmentId =
                        DatabaseSession.optional<Guid> 5 row
                        |> Option.map (DatabaseSession.admitted AttachmentId.create)
                    Outcome =
                        DatabaseSession.value<string> 6 row
                        |> DatabaseSession.admitted (
                            Vocabulary.tryParse DisclosureOutcome.vocabulary
                        )
                    Export = export
                    Page = page
                }
        }

    let upperBound (tx: ReadTx) =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT claimcore.disclosure_trail_upper_bound()"
                    [||]
                    tx.CancellationToken

            return DatabaseSession.value<int64> 0 rows.Head
        }

    let page (tx: ReadTx) (request: DisclosureTrailRequest) before throughId limit =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    ("SELECT id,recorded_at,actor_id,kind,case_id,attachment_id,outcome,export_query,export_filtered,export_paged,export_order,export_grouping,export_row_count,disclosed_count,disclosed_cases,disclosed_people"
                     + " FROM claimcore.read_disclosure_trail($1::uuid,$2::uuid,$3::text,$4::timestamptz,$5::timestamptz,$6::timestamptz,$7::bigint,$8::bigint,$9::integer)"
                     + " ORDER BY recorded_at DESC,id DESC")
                    [|
                        parameter ActorId.value request.ActorId
                        parameter CaseId.value request.CaseId
                        parameter kind request.Kind
                        parameter
                            (fun (instant: DateTimeOffset) -> instant.ToUniversalTime())
                            request.From
                        parameter
                            (fun (instant: DateTimeOffset) -> instant.ToUniversalTime())
                            request.Until
                        parameter fst before
                        parameter snd before
                        box throughId
                        box limit
                    |]
                    tx.CancellationToken

            return List.map event rows
        }
