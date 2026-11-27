namespace ClaimCore.Application.Storage

open System
open System.Data
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Application.Contracts
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
                        tx.Cipher
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

    let private originDescriptor caseId row record =
        let blob =
            DatabaseSession.value<Guid> 0 row
            |> DatabaseSession.admitted AttachmentId.create

        let change = acceptedChange caseId row record

        match change.Value, DatabaseSession.optional<byte array> 4 row with
        | PartValue.Attachment(AttachmentEffect.Added(true, Some descriptor)), Some digest when
            descriptor.Blob = blob
            && DatabaseSession.optional<int64> 3 row =
                Some(AttachmentIntent.length descriptor.Intent)
            && digest.Length = 32
            ->
            descriptor, digest
        | _ -> corrupt ()

    let private originColumns =
        """SELECT b.blob_id,l.operation_id,b.effect_index,b.length,b.digest
        FROM claimcore.attachment_blobs b
        JOIN claimcore.ledger l ON l.sequence=b.operation_sequence"""

    let private origin (tx: ReadTx) caseId blob =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    (originColumns + " WHERE b.case_id=$1 AND b.blob_id=$2")
                    [| family caseId; identity blob |]
                    tx.CancellationToken

            match rows with
            | [ row ] ->
                let! record =
                    OperationStorage.find
                        tx.Connection
                        tx.Cipher
                        (operation row)
                        tx.CancellationToken

                return originDescriptor caseId row record
            | _ -> return corrupt ()
        }

    let private decrypt (cipher: InstallationCipher) caseId blob digest (envelope: byte array) =
        let bytes =
            cipher.DecryptBlob(CaseId.value caseId, AttachmentId.value blob, envelope)

        if cipher.AttachmentDigest bytes <> digest then
            corrupt ()
        else
            bytes

    let private storedBytes (tx: ReadTx) caseId (descriptor: AttachmentDescriptor) digest =
        task {
            let length = AttachmentIntent.length descriptor.Intent
            let storedLength = length + int64 InstallationCipher.envelopeOverhead

            use command =
                DatabaseSession.statement
                    tx.Connection
                    """
                    SELECT payload FROM claimcore.attachment_blobs
                    WHERE case_id=$1 AND blob_id=$2 AND octet_length(payload)=$3
                    """
                    [| family caseId; identity descriptor.Blob; box storedLength |]

            use! reader =
                command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, tx.CancellationToken)

            let! present = reader.ReadAsync(tx.CancellationToken)

            if not present then
                return corrupt ()
            else
                use stream = reader.GetStream 0
                let envelope = Array.zeroCreate (int storedLength)
                do! stream.ReadExactlyAsync(Memory envelope, tx.CancellationToken)
                return decrypt tx.Cipher caseId descriptor.Blob digest envelope
        }

    let read tx caseId (descriptor: AttachmentDescriptor) =
        task {
            let! accepted, digest = origin tx caseId descriptor.Blob

            if not (agrees descriptor.Intent accepted.Intent) then
                return corrupt ()
            else
                let! bytes = storedBytes tx caseId accepted digest

                return
                    VerifiedAttachment.verify descriptor.Intent (ReadOnlyMemory bytes)
                    |> Option.defaultWith corrupt
        }

    let private currentReference (tx: ReadTx) caseId (descriptor: AttachmentDescriptor) =
        task {
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
                    [| family caseId; identity descriptor.Blob |]
                    tx.CancellationToken

            match active with
            | [] -> return None
            | [ row ] ->
                let id =
                    DatabaseSession.value<Guid> 0 row
                    |> DatabaseSession.admitted AttachmentId.create

                let! live = current tx caseId id

                match live |> Option.map _.Value with
                | Some(PartValue.Attachment(AttachmentEffect.Added(_, Some attachment))) when
                    attachment.Blob = descriptor.Blob && agrees descriptor.Intent attachment.Intent
                    ->
                    return Some id
                | _ -> return corrupt ()
            | _ -> return corrupt ()
        }

    let findMetadataMatch (tx: ReadTx) caseId intent =
        task {
            // A preview has only the public SHA-256 intent. Inspect the bounded, authorized
            // set of same-length descriptors in native code without acquiring any blob bytes.
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    (originColumns
                     + " WHERE b.case_id=$1 AND b.length=$2 ORDER BY b.blob_id LIMIT $3")
                    [|
                        family caseId
                        box (AttachmentIntent.length intent)
                        box (AttachmentLimits.RetainedFiles + 1L)
                    |]
                    tx.CancellationToken

            if int64 rows.Length > AttachmentLimits.RetainedFiles then
                return corrupt ()
            else
                let! records =
                    OperationStorage.findMany
                        tx.Connection
                        tx.Cipher
                        (List.map operation rows)
                        tx.CancellationToken

                let matches =
                    rows
                    |> List.choose (fun row ->
                        let descriptor, _ =
                            originDescriptor caseId row (Map.tryFind (operation row) records)

                        if agrees intent descriptor.Intent then
                            Some descriptor
                        else
                            None)

                match matches with
                | [] -> return None
                | [ descriptor ] ->
                    let! current = currentReference tx caseId descriptor
                    return Option.map RetainedAttachmentMetadataMatch current
                | _ -> return corrupt ()
        }

    let findMatch (tx: ReadTx) caseId verified =
        task {
            let digest =
                (VerifiedAttachment.bytes verified).ToArray() |> tx.Cipher.AttachmentDigest

            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    (originColumns + " WHERE b.case_id=$1 AND b.digest=$2")
                    [| family caseId; box digest |]
                    tx.CancellationToken

            match rows with
            | [] -> return None
            | [ row ] ->
                let! record =
                    OperationStorage.find
                        tx.Connection
                        tx.Cipher
                        (operation row)
                        tx.CancellationToken

                let descriptor, storedDigest = originDescriptor caseId row record
                let! current = currentReference tx caseId descriptor
                let! bytes = storedBytes tx caseId descriptor storedDigest

                return
                    RetainedAttachmentMatch.verify
                        verified
                        descriptor.Blob
                        descriptor
                        current
                        (ReadOnlySpan<byte> bytes)
                    |> Option.map Some
                    |> Option.defaultWith corrupt
            | _ -> return corrupt ()
        }

    let private finding kind issue anchor =
        {
            Sequence = anchor |> Option.map (fun (_, _, at) -> EffectPosition.sequence at)
            Finding = kind
            Attachment =
                Some
                    {
                        Issue = issue
                        CaseId = anchor |> Option.map (fun (caseId, _, _) -> caseId)
                        AttachmentId = anchor |> Option.map (fun (_, id, _) -> id)
                        EffectIndex =
                            anchor |> Option.map (fun (_, _, at) -> EffectPosition.index at)
                    }
        }

    let private referencesFinding = finding VerificationFinding.AttachmentReferences
    let private payloadFinding = finding VerificationFinding.AttachmentPayloads

    let private anchor (caseId, id) at = Some(caseId, id, at)

    // Bounded driver draining and statement work, even if the failing row is first.
    let internal evidencePageSize = 4

    let private evidencePage =
        $" WHERE (operation_sequence,effect_index)>($1,$2) ORDER BY operation_sequence,effect_index LIMIT {evidencePageSize}"

    // SQL orders by the permanent accepting position. Compare against the authenticated stream
    // before interpreting each payload, so earlier missing evidence wins over later corruption.
    let private compareEvidence
        connection
        sql
        parameters
        (expected: Map<CaseId * AttachmentId, EffectPosition * 'evidence>)
        finding
        check
        token
        =
        task {
            use entries = (expected |> Map.toSeq |> Seq.sortBy (snd >> fst)).GetEnumerator()

            let mutable hasExpected = entries.MoveNext()
            let mutable more = true
            let mutable failure = None
            let mutable lastSequence = 0L
            let mutable lastIndex = -1

            while more && failure.IsNone do
                let bounds = [| box lastSequence; box lastIndex |]

                use command =
                    DatabaseSession.statement connection sql (Array.append bounds parameters)

                use! reader = command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token)
                let mutable reading = true
                let mutable count = 0

                while reading && failure.IsNone do
                    let! present = reader.ReadAsync token
                    reading <- present

                    if present then
                        count <- count + 1
                        lastSequence <- reader.GetInt64 0
                        lastIndex <- reader.GetInt32 1
                        let ordinal = position lastSequence lastIndex
                        let caseId = CaseId.create (reader.GetGuid 2)
                        let id = AttachmentId.create (reader.GetGuid 3)

                        if hasExpected then
                            let key, (at, value) = entries.Current

                            if ordinal > at then
                                failure <-
                                    Some(finding AttachmentEvidenceIssue.Missing (anchor key at))
                            elif ordinal < at then
                                failure <- Some(finding AttachmentEvidenceIssue.Extra None)
                            elif (caseId, id) <> (Some(fst key), Some(snd key)) then
                                failure <-
                                    Some(finding AttachmentEvidenceIssue.Mismatched (anchor key at))
                            else
                                let! issue = check reader key value token

                                failure <-
                                    issue |> Option.map (fun issue -> finding issue (anchor key at))

                                hasExpected <- entries.MoveNext()
                        else
                            failure <- Some(finding AttachmentEvidenceIssue.Extra None)

                more <- count = evidencePageSize

            if failure.IsNone && hasExpected then
                let key, (at, _) = entries.Current
                failure <- Some(finding AttachmentEvidenceIssue.Missing (anchor key at))

            return
                match failure with
                | Some error -> Error error
                | None -> Ok()
        }

    let checkReferences connection expected token =
        compareEvidence
            connection
            ("SELECT operation_sequence,effect_index,case_id,attachment_id,blob_id FROM claimcore.attachment_references"
             + evidencePage)
            [||]
            expected
            referencesFinding
            (fun reader _ expected _ ->
                task {
                    let stored =
                        if reader.IsDBNull 4 then
                            None
                        else
                            AttachmentId.create (reader.GetGuid 4)

                    return
                        if stored = expected && (stored.IsSome || reader.IsDBNull 4) then
                            None
                        else
                            Some AttachmentEvidenceIssue.Mismatched
                })
            token

    let matchesDescriptors (descriptors: AttachmentDescriptor list) (bytes: byte array) =
        let digest = AttachmentDigest.hash (ReadOnlySpan<byte> bytes)

        descriptors
        |> List.forall (fun descriptor ->
            int64 bytes.Length = AttachmentIntent.length descriptor.Intent
            && digest = AttachmentIntent.digest descriptor.Intent
            && descriptor.Preview =
                AttachmentPreviewProfile.forName
                    (AttachmentIntent.name descriptor.Intent)
                    (ReadOnlySpan<byte> bytes))

    let checkBlobs
        connection
        (cipher: InstallationCipher)
        expected
        (descriptors: Map<CaseId * AttachmentId, AttachmentDescriptor list>)
        token
        =
        compareEvidence
            connection
            ("""SELECT operation_sequence,effect_index,case_id,blob_id,length,
                      digest IS NOT NULL, CASE WHEN octet_length(digest)=32 THEN digest END,
                      octet_length(payload)::bigint,
                      CASE WHEN octet_length(payload)<=$3 THEN payload END
               FROM claimcore.attachment_blobs"""
             + evidencePage)
            [| box (AttachmentLimits.FileBytes + InstallationCipher.envelopeOverhead) |]
            expected
            payloadFinding
            (fun reader (caseId, id) intent token ->
                task {
                    match intent with
                    | Some(descriptor: AttachmentDescriptor) when not (reader.IsDBNull 4) ->
                        let length = reader.GetInt64 4
                        let storedLength = length + int64 InstallationCipher.envelopeOverhead

                        if
                            length <> AttachmentIntent.length descriptor.Intent
                            || reader.IsDBNull 6
                        then
                            return Some AttachmentEvidenceIssue.Mismatched
                        else
                            let digest = reader.GetFieldValue<byte array> 6

                            if
                                reader.IsDBNull 7
                                || reader.GetInt64 7 <> storedLength
                                || reader.IsDBNull 8
                            then
                                return Some AttachmentEvidenceIssue.Mismatched
                            else
                                use stream = reader.GetStream 8
                                let envelope = Array.zeroCreate<byte>(int storedLength)
                                do! stream.ReadExactlyAsync(Memory envelope, token)

                                try
                                    let bytes =
                                        cipher.DecryptBlob(
                                            CaseId.value caseId,
                                            AttachmentId.value id,
                                            envelope
                                        )

                                    return
                                        if
                                            cipher.AttachmentDigest bytes = digest
                                            && matchesDescriptors descriptors[caseId, id] bytes
                                        then
                                            None
                                        else
                                            Some AttachmentEvidenceIssue.Mismatched
                                with :? RecordCorruptException ->
                                    return Some AttachmentEvidenceIssue.Unreadable
                    | None when
                        reader.IsDBNull 4
                        && not (reader.GetBoolean 5)
                        && reader.IsDBNull 7
                        && reader.IsDBNull 8
                        ->
                        return None
                    | _ -> return Some AttachmentEvidenceIssue.Mismatched
                })
            token

    let repair
        connection
        (cipher: InstallationCipher)
        (caseId, id)
        at
        (descriptor: AttachmentDescriptor)
        bytes
        token
        =
        task {
            let digest = cipher.AttachmentDigest bytes
            let envelope = cipher.EncryptBlob(CaseId.value caseId, AttachmentId.value id, bytes)

            let args =
                [|
                    family caseId
                    identity id
                    box (EffectPosition.sequence at |> Sequence.value)
                    box (EffectPosition.index at)
                    box (AttachmentIntent.length descriptor.Intent)
                    box digest
                    box envelope
                |]

            let! updated =
                DatabaseSession.rows
                    connection
                    "SELECT claimcore.repair_attachment_payload($1,$2,$3,$4,$5,$6,$7)"
                    args
                    token

            match updated with
            | [ row ] when DatabaseSession.value<bool> 0 row ->
                let! rows =
                    DatabaseSession.rows
                        connection
                        """SELECT payload FROM claimcore.attachment_blobs
                           WHERE (case_id,blob_id,operation_sequence,effect_index,length,digest)=($1,$2,$3,$4,$5,$6)
                             AND octet_length(payload)::bigint=$5+$7"""
                        (Array.append args[..5] [| box InstallationCipher.envelopeOverhead |])
                        token

                match rows with
                | [ row ] ->
                    let stored =
                        decrypt cipher caseId id digest (DatabaseSession.value<byte array> 0 row)

                    return ReadOnlySpan<byte>(stored).SequenceEqual(ReadOnlySpan<byte>(bytes))
                | _ -> return false
            | _ -> return false
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
