namespace ClaimCore.Postgres

open ClaimCore.Catalog

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

type internal LedgerRow =
    {
        Entry: StoredEntry
        Metadata: LedgerMetadata
    }

module internal StoredEntries =
    let columns =
        "sequence,previous_hash,entry_hash,entry_format,scope,act,operation_id,actor_id,"
        + DatabaseSession.instantExpression "recorded_at"
        + ",subject_id,case_revision,"
        + DatabaseSession.dateExpression "business_date"
        + ",payload_commitment,payload,payload_nonce"

    let private scopes row =
        let target = DatabaseSession.value<Guid> 9 row
        let act = DatabaseSession.value<string> 5 row

        match DatabaseSession.value<string> 4 row with
        | "AUTHORITY" ->
            if not (Convert.IsDBNull row[10]) || not (Convert.IsDBNull row[11]) then
                raise (RecordCorruptException "Invalid authority ledger shape.")

            let kind =
                RecordedAuthorityActs.tryKind act
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid authority act."))

            StoredScope.Authority(target, kind),
            LedgerScope.Authority { TargetActorId = target; Act = kind }
        | "CASE" ->
            let revision = DatabaseSession.value<int64> 10 row

            let kind =
                CaseActKinds.tryParse act
                |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid case act."))

            let date = DatabaseSession.calendarDate 11 row

            StoredScope.Case(target, revision, kind, date),
            LedgerScope.Case
                {
                    CaseId = target
                    Revision = revision
                    Act = kind
                    BusinessDate = date
                }
        | _ -> raise (RecordCorruptException "Invalid ledger scope.")

    let decode installationId row =
        if DatabaseSession.value<int16> 3 row <> int16 RecordVersions.LedgerEntry then
            raise (RecordCorruptException "Unsupported ledger format.")

        let scope, metadataScope = scopes row

        let entry: StoredEntry =
            {
                Sequence = DatabaseSession.value 0 row
                EntryHash = DatabaseSession.value 2 row
                OperationId = DatabaseSession.value 6 row
                ActorId = DatabaseSession.value 7 row
                RecordedAt = DatabaseSession.recordedInstant 8 row
                Scope = scope
                PayloadCommitment = DatabaseSession.value 12 row
                Payload = DatabaseSession.optional<byte array> 13 row
                PayloadNonce = DatabaseSession.optional<byte array> 14 row
            }

        {
            Entry = entry
            Metadata =
                {
                    InstallationId = installationId
                    Sequence = entry.Sequence
                    PreviousHash = DatabaseSession.value 1 row
                    OperationId = entry.OperationId
                    ActorId = entry.ActorId
                    RecordedAt = entry.RecordedAt
                    Scope = metadataScope
                    PayloadCommitment = entry.PayloadCommitment
                }
        }

    let operation connection installationId id token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {columns} FROM claimcore.ledger WHERE operation_id=$1")
                    [| box id |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some((decode installationId row).Entry)
            | _ -> return raise (RecordCorruptException "Duplicate operation identity.")
        }
