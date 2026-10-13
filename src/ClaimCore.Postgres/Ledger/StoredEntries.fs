namespace ClaimCore.Postgres

open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

/// The SQL row retains metadata needed for verification, even when payloads have been erased.
type internal LedgerRow =
    {
        Entry: StoredEntry
        Metadata: LedgerMetadata
        PayloadNonce: byte array option
    }

module internal StoredEntries =
    let columns =
        "sequence,previous_hash,entry_hash,entry_format,kind,operation_id,actor_id,recorded_at,case_id,case_revision,command_kind,business_date,rule_revision,payload_commitment,payload,payload_nonce,request_sha256"

    let actToken =
        function
        | AcceptedAct.Command command -> CommandKinds.token command
        | AcceptedAct.Stewardship StewardshipKind.Void -> "VOID"
        | AcceptedAct.Stewardship StewardshipKind.Reinstate -> "REINSTATE"
        | AcceptedAct.Stewardship StewardshipKind.Erase -> "ERASE"

    let act token =
        match token with
        | "VOID" -> AcceptedAct.Stewardship StewardshipKind.Void
        | "REINSTATE" -> AcceptedAct.Stewardship StewardshipKind.Reinstate
        | "ERASE" -> AcceptedAct.Stewardship StewardshipKind.Erase
        | _ ->
            CommandKinds.all
            |> List.tryFind (CommandKinds.token >> (=) token)
            |> Option.map AcceptedAct.Command
            |> Option.defaultWith (fun () -> invalidOp "Invalid stored act.")

    let private scopes targetId row =
        match DatabaseSession.value<string> 4 row with
        | "AUTHORITY" -> StoredScope.Authority targetId, LedgerScope.Authority
        | "CASE" ->
            let id, revision, act, date =
                DatabaseSession.value<Guid> 8 row,
                DatabaseSession.value<int64> 9 row,
                act (DatabaseSession.value<string> 10 row),
                DatabaseSession.value<DateOnly> 11 row

            let encoded =
                match act with
                | AcceptedAct.Command kind -> LedgerAct.Command kind
                | AcceptedAct.Stewardship kind -> LedgerAct.Stewardship kind

            StoredScope.Case(id, revision, act, date),
            LedgerScope.Case
                {
                    CaseId = id
                    Revision = revision
                    Act = encoded
                    BusinessDate = date
                    RuleRevision = uint16 (DatabaseSession.value<int16> 12 row)
                }
        | _ -> invalidOp "Invalid ledger kind."

    let decode installationId targetId row =
        if DatabaseSession.value<int16> 3 row <> 1s then
            invalidOp "Unsupported ledger format."

        let scope, metadataScope = scopes targetId row

        let entry =
            {
                Sequence = DatabaseSession.value 0 row
                EntryHash = DatabaseSession.value 2 row
                OperationId = DatabaseSession.value 5 row
                ActorId = DatabaseSession.value 6 row
                RecordedAt = DatabaseSession.instant 7 row
                Scope = scope
                Payload = DatabaseSession.optional<byte array> 14 row
                RequestSha256 = DatabaseSession.optional<byte array> 16 row
            }

        {
            Entry = entry
            PayloadNonce = DatabaseSession.optional<byte array> 15 row
            Metadata =
                {
                    InstallationId = installationId
                    Sequence = uint64 entry.Sequence
                    PreviousHash = DatabaseSession.value 1 row
                    OperationId = entry.OperationId
                    ActorId = entry.ActorId
                    RecordedAt = entry.RecordedAt
                    Scope = metadataScope
                    PayloadCommitment = DatabaseSession.value 13 row
                }
        }

    let target connection row token =
        task {
            if DatabaseSession.value<string> 4 row = "CASE" then
                return Guid.Empty
            else
                let bytes = DatabaseSession.value<byte array> 14 row

                let payload =
                    LedgerEntry.decodePayload 262144 bytes
                    |> Result.defaultWith (fun _ -> invalidOp "Invalid authority payload.")

                let request =
                    AuthorityRecord.decode 65536 payload.Request
                    |> Result.defaultWith (fun _ -> invalidOp "Invalid authority request.")

                let principal =
                    PrincipalKey.fromStorage
                        request.Target.Kind
                        request.Target.Issuer
                        request.Target.Subject
                    |> Result.defaultWith (fun _ -> invalidOp "Invalid authority principal.")

                let! actor = Projections.actor connection principal token

                return
                    actor
                    |> Option.map _.ActorId
                    |> Option.defaultWith (fun () -> invalidOp "Authority target is absent.")
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
            | [ row ] ->
                let! targetId = target connection row token
                return Some (decode installationId targetId row).Entry
            | _ -> return invalidOp "Duplicate operation identity."
        }
