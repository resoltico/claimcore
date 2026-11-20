namespace ClaimCore.Application.Storage

open System
open System.Data
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat

type internal RetainedAttachmentMetadataMatch = RetainedAttachmentMetadataMatch of AttachmentId

/// Attachment binary values stay out of root and projection reads.
module internal AttachmentStorage =
    let private corrupt () =
        raise (RecordCorruptException "Attachment evidence is incomplete or inconsistent.")

    let private family caseId = box (CaseId.value caseId)
    let private identity id = box (AttachmentId.value id)

    let private position sequence index =
        let sequence = DatabaseSession.admitted Sequence.create sequence
        DatabaseSession.admitted (EffectPosition.create sequence) index

    let private operation row =
        DatabaseSession.value<Guid> 1 row |> DatabaseSession.admitted OperationId.create

    let private acceptedChange caseId row (record: RetainedOperation option) =
        let id = DatabaseSession.value<Guid> 0 row
        let index = DatabaseSession.value<int> 2 row

        match record |> Option.bind (fun value -> value.Changes |> List.tryItem index) with
        | Some change when
            change.Kind = PartKind.Attachment
            && change.Aggregate = AggregateReference.Family caseId
            && change.PartId = id
            ->
            change
        | _ -> corrupt ()

    let private accepted (tx: ReadTx) caseId row =
        task {
            let! record =
                OperationStorage.find
                    tx.Connection
                    tx.InstallationId
                    (operation row)
                    tx.CancellationToken

            return acceptedChange caseId row record
        }

    let currentMany (tx: ReadTx) caseId (ids: AttachmentId list) =
        task {
            if ids.IsEmpty then
                return Map.empty
            else
                let! rows =
                    DatabaseSession.rows
                        tx.Connection
                        """
                        SELECT DISTINCT ON(e.part_id) e.part_id,l.operation_id,e.effect_index
                        FROM claimcore.ledger_effects e
                        JOIN claimcore.ledger l ON l.sequence=e.operation_sequence
                        WHERE e.aggregate_kind='FAMILY' AND e.aggregate_id=$1
                          AND e.part_kind='ATTACHMENT' AND e.part_id=ANY($2)
                        ORDER BY e.part_id,e.revision DESC
                        """
                        [| family caseId; box (Array.ofList ids |> Array.map AttachmentId.value) |]
                        tx.CancellationToken

                let! records =
                    OperationStorage.findMany
                        tx.Connection
                        tx.InstallationId
                        (List.map operation rows)
                        tx.CancellationToken

                return
                    rows
                    |> List.map (fun row ->
                        let change =
                            acceptedChange caseId row (Map.tryFind (operation row) records)

                        DatabaseSession.admitted AttachmentId.create change.PartId, change)
                    |> Map.ofList
        }

    let current tx caseId id =
        task {
            let! changes = currentMany tx caseId [ id ]
            return Map.tryFind id changes
        }

    let list (tx: ReadTx) caseId =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT attachment_id FROM claimcore.case_attachments WHERE case_id=$1 ORDER BY attachment_id LIMIT $2"
                    [| family caseId; box (AttachmentLimits.RetainedFiles + 1L) |]
                    tx.CancellationToken

            if int64 rows.Length > AttachmentLimits.RetainedFiles then
                return corrupt ()
            else
                let ids =
                    rows
                    |> List.map (
                        DatabaseSession.value<Guid> 0
                        >> DatabaseSession.admitted AttachmentId.create
                    )

                let! changes = currentMany tx caseId ids

                return
                    changes
                    |> Map.values
                    |> Seq.filter (fun change ->
                        match change.Value with
                        | PartValue.Attachment(AttachmentEffect.Added(_, Some _)) -> true
                        | _ -> false)
                    |> Seq.sortByDescending _.Stamp.Position
                    |> Seq.toList
        }

    let usage (tx: ReadTx) caseId =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT count(*),coalesce(sum(length),0)::bigint FROM claimcore.attachment_blobs WHERE case_id=$1 AND digest IS NOT NULL"
                    [| family caseId |]
                    tx.CancellationToken

            match rows with
            | [ row ] ->
                return DatabaseSession.value<int64> 0 row, DatabaseSession.value<int64> 1 row
            | _ -> return corrupt ()
        }

    let private agrees left right =
        AttachmentIntent.length left = AttachmentIntent.length right
        && AttachmentIntent.digest left = AttachmentIntent.digest right

    let private origin (tx: ReadTx) caseId blob =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    """
                    SELECT b.blob_id,l.operation_id,b.effect_index,b.length,b.digest
                    FROM claimcore.attachment_blobs b
                    JOIN claimcore.ledger l ON l.sequence=b.operation_sequence
                    WHERE b.case_id=$1 AND b.blob_id=$2
                    """
                    [| family caseId; identity blob |]
                    tx.CancellationToken

            match rows with
            | [ row ] ->
                let! change = accepted tx caseId row

                match change.Value with
                | PartValue.Attachment(AttachmentEffect.Added(true, Some descriptor)) when
                    descriptor.Blob = blob
                    && DatabaseSession.optional<int64> 3 row =
                        Some(AttachmentIntent.length descriptor.Intent)
                    && DatabaseSession.optional<byte array> 4 row =
                        Some(
                            AttachmentIntent.digest descriptor.Intent
                            |> AttachmentDigest.value
                            |> Convert.FromHexString
                        )
                    ->
                    return descriptor
                | _ -> return corrupt ()
            | _ -> return corrupt ()
        }

    let private storedBytes (tx: ReadTx) caseId (descriptor: AttachmentDescriptor) =
        task {
            let length = AttachmentIntent.length descriptor.Intent

            use command =
                DatabaseSession.statement
                    tx.Connection
                    """
                    SELECT payload FROM claimcore.attachment_blobs
                    WHERE case_id=$1 AND blob_id=$2 AND octet_length(payload)=$3
                    """
                    [| family caseId; identity descriptor.Blob; box length |]

            use! reader =
                command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, tx.CancellationToken)

            let! present = reader.ReadAsync(tx.CancellationToken)

            if not present then
                return corrupt ()
            else
                use stream = reader.GetStream 0
                let bytes = Array.zeroCreate (int length)
                do! stream.ReadExactlyAsync(Memory bytes, tx.CancellationToken)
                return bytes
        }

    let read tx caseId (descriptor: AttachmentDescriptor) =
        task {
            let! accepted = origin tx caseId descriptor.Blob

            if not (agrees descriptor.Intent accepted.Intent) then
                return corrupt ()
            else
                let! bytes = storedBytes tx caseId accepted

                return
                    VerifiedAttachment.verify descriptor.Intent (ReadOnlyMemory bytes)
                    |> Option.defaultWith corrupt
        }

    let private retainedMetadata (tx: ReadTx) caseId digest =
        task {
            let! matches =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT blob_id FROM claimcore.attachment_blobs WHERE case_id=$1 AND digest=$2"
                    [|
                        family caseId
                        box (AttachmentDigest.value digest |> Convert.FromHexString)
                    |]
                    tx.CancellationToken

            match matches with
            | [] -> return None
            | [ row ] ->
                let blob =
                    DatabaseSession.value<Guid> 0 row
                    |> DatabaseSession.admitted AttachmentId.create

                let! descriptor = origin tx caseId blob

                // The accepting links remain authoritative even if list membership is missing.
                let! active =
                    DatabaseSession.rows
                        tx.Connection
                        """
                        SELECT r.attachment_id FROM claimcore.attachment_references r
                        WHERE r.case_id=$1 AND r.blob_id=$2 AND NOT EXISTS(
                            SELECT 1 FROM claimcore.ledger_effects e
                            WHERE e.part_kind='ATTACHMENT' AND e.part_id=r.attachment_id AND e.revision>1
                        ) LIMIT 2
                        """
                        [| family caseId; identity blob |]
                        tx.CancellationToken

                match active with
                | [] -> return Some(descriptor, None)
                | [ row ] ->
                    let id =
                        DatabaseSession.value<Guid> 0 row
                        |> DatabaseSession.admitted AttachmentId.create

                    let! live = current tx caseId id

                    match live |> Option.map _.Value with
                    | Some(PartValue.Attachment(AttachmentEffect.Added(_, Some attachment))) when
                        attachment.Blob = blob && agrees descriptor.Intent attachment.Intent
                        ->
                        return Some(descriptor, Some id)
                    | _ -> return corrupt ()
                | _ -> return corrupt ()
            | _ -> return corrupt ()
        }

    let findMetadataMatch tx caseId intent =
        task {
            let! retained = retainedMetadata tx caseId (AttachmentIntent.digest intent)

            return
                retained
                |> Option.bind (fun (descriptor, current) ->
                    if agrees intent descriptor.Intent then
                        current |> Option.map RetainedAttachmentMetadataMatch
                    else
                        None)
        }

    let findMatch tx caseId verified =
        task {
            let wanted = VerifiedAttachment.intent verified
            let! retained = retainedMetadata tx caseId (AttachmentIntent.digest wanted)

            match retained with
            | None -> return None
            | Some(descriptor, current) ->
                let! bytes = storedBytes tx caseId descriptor

                return
                    RetainedAttachmentMatch.verify
                        verified
                        descriptor.Blob
                        descriptor
                        current
                        (ReadOnlySpan<byte> bytes)
                    |> Option.map Some
                    |> Option.defaultWith corrupt
        }

    let checkReferences
        connection
        (expected: Map<CaseId * AttachmentId, EffectPosition * AttachmentId option>)
        token
        =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT case_id,attachment_id,operation_sequence,effect_index,blob_id FROM claimcore.attachment_references"
                    [||]
                    token

            let actual =
                rows
                |> List.map (fun row ->
                    let caseId =
                        DatabaseSession.value<Guid> 0 row |> DatabaseSession.admitted CaseId.create

                    let id =
                        DatabaseSession.value<Guid> 1 row
                        |> DatabaseSession.admitted AttachmentId.create

                    let position =
                        position
                            (DatabaseSession.value<int64> 2 row)
                            (DatabaseSession.value<int> 3 row)

                    let blob =
                        DatabaseSession.optional<Guid> 4 row
                        |> Option.map (DatabaseSession.admitted AttachmentId.create)

                    (caseId, id), (position, blob))
                |> Map.ofList

            return rows.Length = actual.Count && actual = expected
        }

    let checkBlobs
        connection
        (expected: Map<CaseId * AttachmentId, EffectPosition * AttachmentDescriptor option>)
        token
        =
        task {
            use command =
                DatabaseSession.statement
                    connection
                    """SELECT case_id,blob_id,operation_sequence,effect_index,length,
                        digest IS NOT NULL, CASE WHEN octet_length(digest)=32 THEN digest END,
                        octet_length(payload)::bigint,payload FROM claimcore.attachment_blobs"""
                    [||]

            use! reader = command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token)
            let mutable unmatched = expected
            let mutable matches = true
            let mutable reading = true

            while matches && reading do
                let! found = reader.ReadAsync token
                reading <- found

                if found then
                    let caseId = DatabaseSession.admitted CaseId.create (reader.GetGuid 0)
                    let id = DatabaseSession.admitted AttachmentId.create (reader.GetGuid 1)
                    let actual = position (reader.GetInt64 2) (reader.GetInt32 3)
                    let key = caseId, id

                    match Map.tryFind key unmatched with
                    | Some(position, intent) when position = actual ->
                        unmatched <- Map.remove key unmatched

                        match intent with
                        | Some descriptor when not (reader.IsDBNull 4) ->
                            let length = reader.GetInt64 4

                            let digest =
                                AttachmentIntent.digest descriptor.Intent
                                |> AttachmentDigest.value
                                |> Convert.FromHexString

                            if
                                length <> AttachmentIntent.length descriptor.Intent
                                || reader.IsDBNull 6
                                || reader.GetFieldValue<byte array>(6) <> digest
                                || reader.IsDBNull 7
                                || reader.GetInt64(7) <> length
                                || reader.IsDBNull 8
                            then
                                matches <- false
                            else
                                use stream = reader.GetStream 8
                                let! actualDigest = SHA256.HashDataAsync(stream, token)
                                matches <- actualDigest = digest
                        | None ->
                            matches <-
                                reader.IsDBNull 4
                                && not (reader.GetBoolean 5)
                                && reader.IsDBNull 7
                                && reader.IsDBNull 8
                        | _ -> matches <- false
                    | _ -> matches <- false

            return matches && unmatched.IsEmpty
        }

    let checkProjection connection expected token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT case_id,attachment_id FROM claimcore.case_attachments"
                    [||]
                    token

            let actual =
                rows
                |> List.map (fun row ->
                    let caseId =
                        DatabaseSession.value<Guid> 0 row |> DatabaseSession.admitted CaseId.create

                    let id =
                        DatabaseSession.value<Guid> 1 row
                        |> DatabaseSession.admitted AttachmentId.create

                    caseId, id)
                |> Set.ofList

            return actual.Count = rows.Length && actual = expected
        }

    let rebuildProjection connection (expected: Set<CaseId * AttachmentId>) token =
        task {
            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.case_attachments"
                    [||]
                    token

            for caseId, id in expected do
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_attachments(case_id,attachment_id) VALUES($1,$2)"
                        [| family caseId; identity id |]
                        token

                ()
        }
