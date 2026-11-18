namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal CaseIdentityReads =
    // Reference edits carry the current spelling in the accepted case value, even on key returns.
    let referenceAt caseId revisionCondition =
        $"(SELECT e.retained->>'reference' FROM claimcore.ledger_effects e WHERE e.part_kind='CASE' AND e.part_id={caseId} AND e.retained->>'reference' IS NOT NULL {revisionCondition} ORDER BY e.revision DESC LIMIT 1)"

    let resolve (tx: ReadTx) selector =
        task {
            let key, identity =
                match selector with
                | CaseSelector.Id id -> None, Some id
                | CaseSelector.Reference raw ->
                    CaseReference.parse raw |> Result.toOption |> Option.map CaseReference.identity,
                    None

            if key.IsNone && identity.IsNone then
                return None
            else
                let! rows =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT part_id FROM claimcore.ledger_effects WHERE part_kind=$1 AND (($2::uuid IS NOT NULL AND part_id=$2 AND revision=1) OR ($3::text IS NOT NULL AND upper(natural_key COLLATE \"C\")=$3))"
                        [|
                            box (PartKind.token PartKind.Case)
                            DatabaseSession.parameter (identity |> Option.map CaseId.value)
                            DatabaseSession.parameter key
                        |]
                        tx.CancellationToken

                match rows with
                | [] -> return None
                | [ row ] ->
                    return
                        Some(
                            DatabaseSession.value<System.Guid> 0 row
                            |> DatabaseSession.admitted CaseId.create
                        )
                | _ -> return raise (RecordCorruptException "Family selection is ambiguous.")
        }

    let referenceBefore (tx: ReadTx) id revision =
        task {
            let expression = referenceAt "$1" "AND e.revision < $2"

            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    ("SELECT " + expression)
                    [| box (CaseId.value id); box (Revision.value revision) |]
                    tx.CancellationToken

            return
                match rows with
                | [ row ] ->
                    DatabaseSession.optional<string> 0 row
                    |> Option.map (fun value ->
                        CaseReference.parse value
                        |> Result.defaultWith (fun _ ->
                            raise (RecordCorruptException "Accepted case reference is invalid.")))
                | _ -> raise (RecordCorruptException "Accepted case reference is ambiguous.")
        }

    let matchedEarlier (tx: ReadTx) id current selector =
        task {
            match selector with
            | CaseSelector.Id _ -> return None
            | CaseSelector.Reference input ->
                match CaseReference.parse input with
                | Error _ -> return None
                | Ok selected when selected = current -> return None
                | Ok selected when CaseReference.identity current <> CaseReference.identity selected ->
                    return Some selected
                | Ok selected ->
                    let! rows =
                        DatabaseSession.rows
                            tx.Connection
                            "SELECT EXISTS (SELECT FROM claimcore.ledger_effects WHERE part_kind=$1 AND part_id=$2 AND retained->>'reference'=$3)"
                            [|
                                box (PartKind.token PartKind.Case)
                                box (CaseId.value id)
                                box (CaseReference.format selected)
                            |]
                            tx.CancellationToken

                    return
                        if DatabaseSession.value<bool> 0 rows.Head then
                            Some selected
                        else
                            None
        }
