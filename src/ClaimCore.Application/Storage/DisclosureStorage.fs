namespace ClaimCore.Application.Storage

open System
open System.Text
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json

module internal DisclosureStorage =
    let private kind = Vocabulary.token DisclosureKind.vocabulary

    let private selectionText (selection: Map<string, string>) =
        selection
        |> Map.toList
        |> List.map (fun (name, value) -> name, JsonValue.String value)
        |> JsonValue.Object
        |> CanonicalWriter.bytes
        |> Encoding.UTF8.GetString

    let private parameter project value =
        value |> Option.map project |> DatabaseSession.parameter

    let append connection (event: DisclosureEvent) token =
        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.disclosure_trail(actor_id,kind,case_id,attachment_id,outcome,export_query,export_selection,export_row_count) VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8)"
            [|
                box (ActorId.value event.ActorId)
                box (kind event.Kind)
                parameter CaseId.value event.CaseId
                parameter AttachmentId.value event.AttachmentId
                box (Vocabulary.token DisclosureOutcome.vocabulary event.Outcome)
                parameter _.Query event.Export
                parameter
                    (fun (value: DisclosureExport) -> selectionText value.Selection)
                    event.Export
                parameter _.RowCount event.Export
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

    let private selection (text: string) =
        let bytes = Encoding.UTF8.GetBytes text

        match StrictJson.read bytes.Length bytes with
        | Ok(JsonValue.Object fields) ->
            fields
            |> List.map (function
                | name, JsonValue.String value -> name, value
                | _ -> corrupt ())
            |> Map.ofList
        | _ -> corrupt ()

    let private event row =
        let export =
            match
                DatabaseSession.optional<string> 7 row,
                DatabaseSession.optional<string> 8 row,
                DatabaseSession.optional<int> 9 row
            with
            | None, None, None -> None
            | Some query, Some selected, Some count when count >= 0 ->
                Some
                    {
                        Query = query
                        Selection = selection selected
                        RowCount = count
                    }
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
                }
        }

    let page (tx: ReadTx) (request: DisclosureTrailRequest) before limit =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    ("SELECT id,recorded_at,actor_id,kind,case_id,attachment_id,outcome,export_query,export_selection::text,export_row_count"
                     + " FROM claimcore.read_disclosure_trail($1::uuid,$2::uuid,$3::text,$4::timestamptz,$5::timestamptz,$6::timestamptz,$7::bigint,$8::integer)"
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
                        box limit
                    |]
                    tx.CancellationToken

            return List.map event rows
        }
