namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal CaseIdentityReads =
    // The opening effect owns the permanent reference.
    let openingReference caseId =
        $"(SELECT e.retained->>'reference' FROM claimcore.ledger_effects e WHERE e.part_kind='CASE' AND e.part_id={caseId} AND e.revision=1)"

    let private findId (tx: ReadTx) identity key =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT part_id FROM claimcore.ledger_effects WHERE part_kind=$1 AND (($2::uuid IS NOT NULL AND part_id=$2 AND revision=1) OR ($3::text IS NOT NULL AND revision=1 AND upper(natural_key COLLATE \"C\")=$3))"
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

    let referenceOwner tx reference =
        findId tx None (Some(CaseReference.identity reference))

    let resolve tx =
        function
        | CaseSelector.Id id -> findId tx (Some id) None
        | CaseSelector.Reference raw ->
            match CaseReference.parse raw with
            | Ok reference -> referenceOwner tx reference
            | Error _ -> System.Threading.Tasks.Task.FromResult None
