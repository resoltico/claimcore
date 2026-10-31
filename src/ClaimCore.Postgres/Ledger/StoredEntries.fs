namespace ClaimCore.Postgres


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
    let position sequence bytes =
        let sequence = DatabaseSession.admitted Sequence.create sequence

        EntryHash.ofBytes bytes
        |> Option.map (LedgerPosition.create sequence)
        |> Option.defaultWith (fun () ->
            raise (RecordCorruptException "Invalid stored ledger position."))

    let columns =
        "sequence,previous_hash,entry_hash,entry_format,subject_kind,act,operation_id,actor_id,"
        + DatabaseSession.instantExpression "recorded_at"
        + ",subject_id,case_revision,"
        + DatabaseSession.dateExpression "business_date"
        + ",payload_commitment,payload,payload_nonce,creation_reference"

    let private subjects row =
        let target = DatabaseSession.value<Guid> 9 row
        let act = DatabaseSession.value<string> 5 row

        match DatabaseSession.value<string> 4 row with
        | "AUTHORITY" ->
            let target = DatabaseSession.admitted ActorId.create target

            if not (Convert.IsDBNull row[10]) || not (Convert.IsDBNull row[11]) then
                raise (RecordCorruptException "Invalid authority ledger shape.")

            let kind =
                DurableNames.tryAuthorityAct act
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid authority act."))

            EntryTarget.Authority(target, kind),
            RecordedTarget.Authority { TargetActorId = target; Act = kind }
        | "CASE" ->
            let target = DatabaseSession.admitted CaseId.create target

            let revision =
                DatabaseSession.value<int64> 10 row |> DatabaseSession.admitted Revision.create

            let kind =
                DurableNames.tryCaseAct act
                |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid case act."))

            let date = DatabaseSession.calendarDate 11 row

            EntryTarget.Case(target, revision, kind, date),
            RecordedTarget.Case
                {
                    CaseId = target
                    Revision = revision
                    Act = kind
                    BusinessDate = date
                }
        | _ -> raise (RecordCorruptException "Invalid ledger subject.")

    let decode installationId row =
        if DatabaseSession.value<int16> 3 row <> int16 RecordVersions.LedgerEntry then
            raise (RecordCorruptException "Unsupported ledger format.")

        let subject, metadataSubject = subjects row

        let entry: StoredEntry =
            {
                Sequence = DatabaseSession.value 0 row |> DatabaseSession.admitted Sequence.create
                EntryHash = DatabaseSession.value 2 row
                OperationId =
                    DatabaseSession.value 6 row |> DatabaseSession.admitted OperationId.create
                ActorId = DatabaseSession.value 7 row |> DatabaseSession.admitted ActorId.create
                RecordedAt = DatabaseSession.recordedInstant 8 row
                Subject = subject
                CreationReference =
                    DatabaseSession.optional<string> 15 row
                    |> Option.map (
                        CaseReference.parse
                        >> Result.defaultWith (fun _ ->
                            raise (RecordCorruptException "Invalid retained reference."))
                    )
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
                    Subject = metadataSubject
                    CreationReference = entry.CreationReference
                    PayloadCommitment = entry.PayloadCommitment
                }
        }

    let operation connection installationId id token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {columns} FROM claimcore.ledger WHERE operation_id=$1")
                    [| box (OperationId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some((decode installationId row).Entry)
            | _ -> return raise (RecordCorruptException "Duplicate operation identity.")
        }
