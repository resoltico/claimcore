namespace ClaimCore.Application.Reads

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Execution

module internal TimelineStorage =
    type PageQuery =
        {
            CaseId: CaseId
            Filter: TimelineFilter option
            Range: ChangeRange option
            BeforePosition: EffectPosition option
            Limit: int
        }

    let stages connection installation parent token =
        task {
            let kind = PartKind.token PartKind.Case

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.part_kind=$2 AND e.part_id=$1 ORDER BY e.revision DESC LIMIT 1"
                    [| box (CaseId.value parent); box kind |]
                    token

            let! entries = StoredEntries.bind connection installation rows token

            match entries with
            | [] -> return None
            | [ entry ] ->
                let! dates =
                    DatabaseSession.rows
                        connection
                        ("SELECT "
                         + DatabaseSession.dateExpression
                             "(SELECT l.business_date FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.part_kind=$2 AND e.part_id=$1 AND e.revision=1)"
                         + ","
                         + DatabaseSession.dateExpression
                             "(SELECT l.business_date FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence JOIN claimcore.ledger_effects p ON p.part_kind=e.part_kind AND p.part_id=e.part_id AND p.revision=e.revision-1 WHERE e.part_kind=$2 AND e.part_id=$1 AND e.retained#>>'{handling,kind}'=$3 AND p.retained#>>'{handling,kind}' IS DISTINCT FROM $3 ORDER BY e.operation_sequence DESC LIMIT 1)"
                         + ","
                         + CaseIdentityReads.referenceAt "$1" "")
                        [|
                            box (CaseId.value parent)
                            box kind
                            box (CaseStatus.token CaseStatus.Closed)
                        |]
                        token

                let row =
                    match dates with
                    | [ row ] -> row
                    | _ -> raise (RecordCorruptException "Case stage basis is absent.")

                let reference =
                    DatabaseSession.value<string> 2 row
                    |> CaseReference.parse
                    |> Result.defaultWith (fun _ ->
                        raise (RecordCorruptException "Current case reference is invalid."))

                let snapshot =
                    PartChange.caseSnapshot reference entry.Change
                    |> Option.defaultWith (fun () ->
                        raise (
                            RecordCorruptException "Current case version has unavailable content."
                        ))

                if snapshot.CaseId <> parent then
                    raise (RecordCorruptException "Current case stage belongs to another family.")

                return
                    Some
                        {
                            CaseStageFacts.Revision = snapshot.Revision
                            Snapshot = snapshot
                            OpenedOn = DatabaseSession.calendarDate 0 row |> CalendarDate.ofDate
                            ClosedOn =
                                DatabaseSession.optionalCalendarDate 1 row
                                |> Option.map CalendarDate.ofDate
                            Disposition = PartChange.disposition entry.Change
                            RecordedAt = entry.RecordedAt
                            RecordedBy = ProjectionRows.familyActor entry.Authority
                        }
            | _ -> return raise (RecordCorruptException "Current case stage is duplicated.")
        }

    let page connection installation (query: PageQuery) token =
        task {
            let kinds =
                query.Filter
                |> Option.map (TimelineFilter.parts >> List.map PartKind.token >> List.toArray)

            let rangeKind, rangeId, low, high =
                match query.Range with
                | None -> None, None, 0L, 0L
                | Some range ->
                    Some(PartKind.token (ChangeEntity.partKind range.Entity)),
                    Some(ChangeEntity.partId query.CaseId range.Entity),
                    Revision.value range.ExpectedRevision,
                    Revision.value range.ActualRevision

            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.aggregate_kind='FAMILY' AND e.aggregate_id=$1 AND ($2::text[] IS NULL OR e.part_kind=ANY($2)) AND ($3::bigint IS NULL OR (e.operation_sequence,e.effect_index)<($3,$4)) AND ($5::text IS NULL OR (e.part_kind=$5 AND e.part_id=$6 AND e.revision>$7 AND e.revision<=$8)) ORDER BY e.operation_sequence DESC,e.effect_index DESC LIMIT $9"
                    [|
                        box (CaseId.value query.CaseId)
                        DatabaseSession.parameter kinds
                        DatabaseSession.parameter (
                            query.BeforePosition
                            |> Option.map (EffectPosition.sequence >> Sequence.value)
                        )
                        DatabaseSession.parameter (
                            query.BeforePosition |> Option.map EffectPosition.index
                        )
                        DatabaseSession.parameter rangeKind
                        DatabaseSession.parameter rangeId
                        box low
                        box high
                        box (query.Limit + 1)
                    |]
                    token

            let! entries = StoredEntries.bind connection installation rows token
            let visible = List.truncate query.Limit entries

            return
                {
                    Page.Items = visible
                    Next =
                        if entries.Length > query.Limit then
                            visible |> List.tryLast |> Option.map _.Position
                        else
                            None
                }
        }

    let previous connection installation (entries: StoredEffect list) token =
        task {
            if entries.Length > Paging.MaximumTimelinePageSize then
                invalidArg (nameof entries) "Use one bounded timeline page."

            let mutable result = Map.empty

            for entry in entries do
                let! previous = OperationStorage.previous connection installation entry.Change token

                match previous with
                | Some previous -> result <- Map.add entry.Position previous result
                | None -> ()

            return result
        }
