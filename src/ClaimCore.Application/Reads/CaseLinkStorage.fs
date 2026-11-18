namespace ClaimCore.Application.Reads

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage

module internal CaseLinkStorage =
    let private identifier make index row =
        DatabaseSession.value<Guid> index row |> DatabaseSession.admitted make

    let private note index row =
        DatabaseSession.optional<string> index row
        |> Option.map (
            NoteText.parse
            >> Result.defaultWith (fun _ ->
                raise (RecordCorruptException "A related case note is invalid."))
        )

    let projection (row: obj array) : LinkProjection =
        let target =
            DatabaseSession.optional<Guid> 2 row
            |> Option.map (DatabaseSession.admitted CaseId.create)

        let note = note 5 row

        if target.IsNone && note.IsSome then
            raise (RecordCorruptException "An unavailable link retained a note.")

        {
            Id = identifier LinkId.create 0 row
            Source = identifier CaseId.create 1 row
            Revision =
                DatabaseSession.value<int64> 3 row |> DatabaseSession.admitted Revision.create
            Value =
                {
                    Removed = DatabaseSession.value<bool> 4 row
                    Content = target |> Option.map (fun target -> { Target = target; Note = note })
                }
        }

    let projections connection token =
        DatabaseSession.rows
            connection
            "SELECT link_id,source_case_id,target_case_id,revision,removed,note FROM claimcore.case_links"
            [||]
            token

    let incoming (tx: ReadTx) selection target =
        task {
            let source = CaseSelectionSql.predicate selection
            let kind = CaseSelectionSql.suffix 1
            let targetParameter = CaseSelectionSql.suffix 2
            let limit = CaseSelectionSql.suffix 3

            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    ($"SELECT l.link_id,c.case_id,c.case_reference,c.state,c.status,l.note,count(*) OVER() FROM claimcore.case_links l JOIN claimcore.cases c ON c.case_id=l.source_case_id JOIN claimcore.ledger_effects e ON e.part_kind={kind} AND e.part_id=l.link_id AND e.revision=1 WHERE l.target_case_id={targetParameter} AND NOT l.removed AND {source} ORDER BY e.operation_sequence DESC,e.effect_index DESC LIMIT {limit}")
                    (Array.append
                        (CaseSelectionSql.parameters selection)
                        [|
                            box (PartKind.token PartKind.Link)
                            box (CaseId.value target)
                            box RelatedCase.MaximumIncomingShown
                        |])
                    tx.CancellationToken

            let parse vocabulary value =
                Vocabulary.tryParse vocabulary value
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid related case state."))

            let items =
                rows
                |> List.map (fun row ->
                    {
                        RelatedCaseRead.Id = identifier LinkId.create 0 row
                        Case =
                            Some
                                {
                                    CaseId = identifier CaseId.create 1 row
                                    Reference =
                                        DatabaseSession.value<string> 2 row
                                        |> CaseReference.parse
                                        |> Result.defaultWith (fun _ ->
                                            raise (
                                                RecordCorruptException
                                                    "Invalid related case reference."
                                            ))
                                    Lifecycle =
                                        DatabaseSession.value<string> 3 row
                                        |> parse Lifecycle.vocabulary
                                    Status =
                                        DatabaseSession.optional<string> 4 row
                                        |> Option.map (parse CaseStatus.vocabulary)
                                }
                        Note = note 5 row
                        CanRemove = false
                    })

            return
                items,
                (rows
                 |> List.tryHead
                 |> Option.map (DatabaseSession.value<int64> 6)
                 |> Option.defaultValue 0L)
        }
