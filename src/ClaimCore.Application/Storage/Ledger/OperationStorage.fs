namespace ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Text
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json
open ClaimCore.RecordFormat

/// Complete-root persistence. No effect or compartment can be appended independently.
module internal OperationStorage =
    let private text value =
        CanonicalWriter.bytes value |> Encoding.UTF8.GetString

    let private json (bytes: byte array) =
        StrictJson.read OperationRoot.MaximumPublicMetadataBytes bytes
        |> Result.defaultWith (fun _ -> invalidOp "Invalid encoded operation metadata.")

    let private owner =
        function
        | CompartmentOwner.Family id -> "FAMILY", CaseId.value id
        | CompartmentOwner.Profile id -> "PROFILE", ActorId.value id

    let private parametersWithLink
        (cipher: InstallationCipher)
        attachmentLink
        (root: OperationRoot)
        =
        let header = root.Header

        let effects =
            OperationRoot.effectBytes root
            |> List.map (snd >> json)
            |> function
                | [ JsonValue.Object members ] when Option.isSome attachmentLink ->
                    [ JsonValue.Object(("attachmentLink", Option.get attachmentLink) :: members) ]
                | rows when Option.isNone attachmentLink -> rows
                | _ -> invalidOp "An attachment link must belong to one accepting effect."
            |> JsonValue.Array
            |> text

        let compartments =
            root.Structure.Compartments
            |> List.map (fun compartment ->
                let descriptor = compartment.Descriptor
                let kind, identity = owner descriptor.Owner

                let nonce, fingerprint, payload =
                    match Compartment.content compartment with
                    | Some(nonce, payload) ->
                        let sequence = Sequence.value header.Sequence

                        let salt =
                            cipher.EncryptCompartmentSalt(
                                sequence,
                                descriptor.Ordinal,
                                kind,
                                identity,
                                nonce
                            )

                        let bytes =
                            cipher.EncryptCompartment(
                                sequence,
                                descriptor.Ordinal,
                                kind,
                                identity,
                                payload
                            )

                        JsonValue.String(Convert.ToHexStringLower salt),
                        JsonValue.String(Convert.ToHexStringLower(cipher.SaltFingerprint nonce)),
                        JsonValue.String(Convert.ToHexStringLower bytes)
                    | None -> JsonValue.Null, JsonValue.Null, JsonValue.Null

                JsonValue.Object
                    [
                        "ordinal", JsonValue.Integer(int64 descriptor.Ordinal)
                        "owner",
                        JsonValue.Object
                            [
                                "kind", JsonValue.String kind
                                "id", JsonValue.String(identity.ToString("D"))
                            ]
                        "commitment",
                        JsonValue.String(Convert.ToHexStringLower compartment.Commitment)
                        "nonce", nonce
                        "nonceFingerprint", fingerprint
                        "payload", payload
                    ])
            |> JsonValue.Array
            |> text

        [|
            box header.InstallationId
            box (Sequence.value header.Sequence)
            box (EntryHash.toBytes header.PreviousHash)
            box (EntryHash.toBytes root.Hash)
            box (int16 InstallationFormat.Current)
            box (OperationId.value header.OperationId)
            DatabaseSession.parameter (
                header.Authority |> Authority.actorId |> Option.map ActorId.value
            )
            box header.RecordedAt
            DatabaseSession.parameter (
                header.BusinessDate
                |> Option.map (fun date ->
                    date.ToString(
                        CalendarDate.ExactFormat,
                        Globalization.CultureInfo.InvariantCulture
                    ))
            )
            box (
                match root.Structure.Effects.Head.Change.Aggregate with
                | AggregateReference.Family _ -> "FAMILY"
                | AggregateReference.Person _ -> "PERSON"
            )
            box (TaskId.token root.Structure.Task)
            box (Encoding.UTF8.GetString(OperationRoot.structureBytes root))
            box effects
            box compartments
        |]

    let parameters cipher root = parametersWithLink cipher None root

    let attachmentParameters
        (cipher: InstallationCipher)
        (entry: RetainedOperation)
        (attachment: VerifiedAttachment)
        =
        match entry.Changes with
        | [ {
                Aggregate = AggregateReference.Family caseId
                Value = PartValue.Attachment(AttachmentEffect.Added(introduces, Some descriptor))
            } ] ->
            let verified = VerifiedAttachment.intent attachment
            let length = AttachmentIntent.length descriptor.Intent

            if
                length <> AttachmentIntent.length verified
                || AttachmentIntent.digest descriptor.Intent <> AttachmentIntent.digest verified
            then
                invalidOp "Verified bytes do not agree with the accepted attachment descriptor."

            let bytes = (VerifiedAttachment.bytes attachment).ToArray()
            let blobId = AttachmentId.value descriptor.Blob

            let link =
                JsonValue.Object
                    [
                        "blobId", JsonValue.String(blobId.ToString("D"))
                        "length", JsonValue.Integer length
                        "keyedDigest",
                        JsonValue.String(Convert.ToHexStringLower(cipher.AttachmentDigest bytes))
                    ]

            let binary =
                if introduces then
                    box (cipher.EncryptBlob(CaseId.value caseId, blobId, bytes))
                else
                    DatabaseSession.nullValue

            Array.append (parametersWithLink cipher (Some link) entry.Root) [| binary |]
        | _ -> invalidOp "Verified attachment bytes require an accepted attachment descriptor."

    let private appendParameters connection values token =
        DatabaseSession.execute
            connection
            "SELECT claimcore.append_operation($1,$2,$3,$4,$5,$6,$7::uuid,$8,$9::text::date,$10,$11,$12::jsonb,$13::jsonb,$14::jsonb,$15::bytea)"
            values
            token

    let append connection cipher root token =
        appendParameters
            connection
            (Array.append (parameters cipher root) [| DatabaseSession.nullValue |])
            token

    let appendWithAttachment connection cipher entry attachment token =
        appendParameters connection (attachmentParameters cipher entry attachment) token

    let private corrupt () =
        raise (RecordCorruptException "Invalid retained operation structure or contents.")

    let private admitted result =
        Result.defaultWith (fun _ -> corrupt ()) result

    let private compartment (cipher: InstallationCipher option) sequence row =
        let identity = DatabaseSession.value<Guid> 2 row
        let kind = DatabaseSession.value<string> 1 row

        let owner =
            match kind with
            | "FAMILY" -> CompartmentOwner.Family(DatabaseSession.admitted CaseId.create identity)
            | "PROFILE" ->
                CompartmentOwner.Profile(DatabaseSession.admitted ActorId.create identity)
            | _ -> corrupt ()

        let ordinal = DatabaseSession.value<int> 0 row
        let descriptor = CompartmentDescriptor.create ordinal owner |> admitted

        let nonce, payload =
            match
                DatabaseSession.optional<byte array> 4 row,
                DatabaseSession.optional<byte array> 5 row,
                DatabaseSession.optional<byte array> 6 row
            with
            | None, None, None -> None, None
            | Some salt, Some fingerprint, Some payload ->
                match cipher with
                | None -> corrupt ()
                | Some cipher ->
                    let nonce =
                        cipher.DecryptCompartmentSalt(sequence, ordinal, kind, identity, salt)

                    if cipher.SaltFingerprint nonce <> fingerprint then
                        corrupt ()
                    else
                        Some nonce,
                        Some(cipher.DecryptCompartment(sequence, ordinal, kind, identity, payload))
            | _ -> corrupt ()

        Compartment.restore descriptor (DatabaseSession.value<byte array> 3 row) nonce payload
        |> admitted

    let private rootColumns =
        "sequence,previous_hash,entry_hash,installation_format,actor_id,recorded_at,"
        + DatabaseSession.dateExpression "business_date"
        + ",structure::text,task"

    let private effectColumns =
        "effect_index,(jsonb_build_object('aggregate',jsonb_build_object('kind',aggregate_kind,'id',aggregate_id),'part',jsonb_build_object('kind',part_kind,'id',part_id),'revision',revision,'naturalKey',natural_key,'retained',retained,'erases',CASE WHEN erases_kind IS NOT NULL THEN jsonb_build_object('kind',erases_kind,'id',erases_id) END,'compartment',compartment) - array_remove(ARRAY[CASE WHEN natural_key IS NULL THEN 'naturalKey' END,CASE WHEN retained IS NULL THEN 'retained' END,CASE WHEN erases_kind IS NULL THEN 'erases' END,CASE WHEN compartment IS NULL THEN 'compartment' END],NULL))::text"

    let private contentColumns includePrivate =
        if includePrivate then
            "ordinal,owner_kind,owner_id,commitment,nonce,nonce_fingerprint,payload"
        else
            "ordinal,owner_kind,owner_id,commitment,NULL::bytea,NULL::bytea,NULL::bytea"

    let private restoreRecord installation cipher operation row effectRows contentRows =
        if DatabaseSession.value<int16> 3 row <> int16 InstallationFormat.Current then
            corrupt ()

        let header =
            {
                OperationHeader.InstallationId = installation
                Sequence =
                    DatabaseSession.value<int64> 0 row |> DatabaseSession.admitted Sequence.create
                PreviousHash =
                    DatabaseSession.value<byte array> 1 row
                    |> EntryHash.ofBytes
                    |> Option.defaultWith corrupt
                OperationId = operation
                Authority =
                    DatabaseSession.optional<Guid> 4 row
                    |> Option.map (DatabaseSession.admitted ActorId.create)
                    |> Authority.ofActorId
                RecordedAt = DatabaseSession.instant 5 row
                BusinessDate = DatabaseSession.optionalCalendarDate 6 row
            }

        let effects =
            effectRows
            |> List.map (fun row ->
                DatabaseSession.value<int> 0 row,
                Encoding.UTF8.GetBytes(DatabaseSession.value<string> 1 row))

        let hash =
            DatabaseSession.value<byte array> 2 row
            |> EntryHash.ofBytes
            |> Option.defaultWith corrupt

        OperationRoot.restore
            header
            (DatabaseSession.value<string> 8 row)
            (Encoding.UTF8.GetBytes(DatabaseSession.value<string> 7 row))
            effects
            (List.map (compartment cipher (Sequence.value header.Sequence)) contentRows)
            hash
        |> admitted
        |> RetainedOperation.restore
        |> admitted

    let private read (cipher: InstallationCipher option) connection installation operation token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ("SELECT " + rootColumns + " FROM claimcore.ledger WHERE operation_id=$1")
                    [| box (OperationId.value operation) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                let sequence = DatabaseSession.value<int64> 0 row
                let parameters = [| box sequence |]

                let! effects =
                    DatabaseSession.rows
                        connection
                        ("SELECT "
                         + effectColumns
                         + " FROM claimcore.ledger_effects WHERE operation_sequence=$1 ORDER BY effect_index")
                        parameters
                        token

                let! contents =
                    DatabaseSession.rows
                        connection
                        ("SELECT "
                         + contentColumns cipher.IsSome
                         + " FROM claimcore.ledger_compartments WHERE operation_sequence=$1 ORDER BY ordinal")
                        parameters
                        token

                return Some(restoreRecord installation cipher operation row effects contents)
            | _ -> return corrupt ()
        }

    let find connection (cipher: InstallationCipher) operation token =
        read (Some cipher) connection cipher.InstallationId operation token

    /// Commitments authenticate public structure without acquiring private compartment bytes.
    let findStructure connection installation operation token =
        read None connection installation operation token

    /// Reuse a retained root within its acquisition. A structure-only read cannot supply private data.
    let acquire (tx: ReadTx) includePrivate absent cache operation =
        task {
            match Map.tryFind operation cache with
            | Some(record, loadedPrivate) when loadedPrivate || not includePrivate ->
                return record, cache, false
            | _ ->
                let! record =
                    if includePrivate then
                        find tx.Connection tx.Cipher operation tx.CancellationToken
                    else
                        findStructure tx.Connection tx.InstallationId operation tx.CancellationToken

                let record =
                    record |> Option.defaultWith (fun () -> raise (RecordCorruptException absent))

                return record, Map.add operation (record, includePrivate) cache, true
        }

    /// A selected metadata page hydrates full authenticated records in a fixed number of
    /// queries. Attachment binary payloads belong to a separate store and are not acquired here.
    let prepareMany connection (cipher: InstallationCipher) (operations: Guid list) token =
        task {
            let identities = operations |> List.distinct |> List.toArray

            if identities.Length = 0 then
                return Map.empty
            elif identities.Length > 128 then
                return
                    invalidArg
                        (nameof operations)
                        "Operation hydration exceeds the bounded metadata batch."
            else
                let! roots =
                    DatabaseSession.rows
                        connection
                        ("SELECT "
                         + rootColumns
                         + ",operation_id FROM claimcore.ledger WHERE operation_id=ANY($1)")
                        [| box identities |]
                        token

                let scope = roots |> List.map (DatabaseSession.value<int64> 0) |> List.toArray

                if scope.Length = 0 then
                    return Map.empty
                else
                    let parameters = [| box scope |]

                    let! effects =
                        DatabaseSession.rows
                            connection
                            ("SELECT "
                             + effectColumns
                             + ",operation_sequence FROM claimcore.ledger_effects WHERE operation_sequence=ANY($1) ORDER BY operation_sequence,effect_index")
                            parameters
                            token

                    let! contents =
                        DatabaseSession.rows
                            connection
                            ("SELECT "
                             + contentColumns true
                             + ",operation_sequence FROM claimcore.ledger_compartments WHERE operation_sequence=ANY($1) ORDER BY operation_sequence,ordinal")
                            parameters
                            token

                    let group ordinal =
                        List.groupBy (DatabaseSession.value<int64> ordinal) >> Map.ofList

                    let effectsBySequence = group 2 effects
                    let contentsBySequence = group 7 contents

                    // Keep ambiguity at its own identity so ordered verification can report
                    // an earlier broken link or invalid root before forcing this restoration.
                    return
                        roots
                        |> List.groupBy (DatabaseSession.value<Guid> 9)
                        |> List.map (fun (operation, matches) ->
                            operation,
                            fun () ->
                                match matches with
                                | [ root ] ->
                                    let sequence = DatabaseSession.value<int64> 0 root

                                    let rows =
                                        Map.tryFind sequence effectsBySequence
                                        |> Option.defaultValue []

                                    let compartments =
                                        Map.tryFind sequence contentsBySequence
                                        |> Option.defaultValue []

                                    restoreRecord
                                        cipher.InstallationId
                                        (Some cipher)
                                        (DatabaseSession.admitted OperationId.create operation)
                                        root
                                        rows
                                        compartments
                                | _ -> corrupt ())
                        |> Map.ofList
        }

    let findMany connection cipher operations token =
        task {
            let! prepared =
                prepareMany connection cipher (List.map OperationId.value operations) token

            return
                prepared
                |> Map.toSeq
                |> Seq.map (fun (operation, restore) ->
                    DatabaseSession.admitted OperationId.create operation, restore ())
                |> Map.ofSeq
        }

    let previous connection cipher (change: PartChange) token =
        task {
            if change.Revision = Revision.first then
                return None
            else
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.part_kind=$1 AND e.part_id=$2 AND e.revision=$3"
                        [|
                            box (PartKind.token change.Kind)
                            box change.PartId
                            box (Revision.value change.Revision - 1L)
                        |]
                        token

                match rows with
                | [ row ] ->
                    let operation =
                        DatabaseSession.value<Guid> 0 row
                        |> DatabaseSession.admitted OperationId.create

                    let! record = find connection cipher operation token

                    let previous =
                        record
                        |> Option.bind (fun record ->
                            record.Changes |> List.tryItem (DatabaseSession.value<int> 1 row))

                    match previous with
                    | Some previous when
                        previous.Kind = change.Kind
                        && previous.PartId = change.PartId
                        && previous.Aggregate = change.Aggregate
                        && Revision.value previous.Revision + 1L = Revision.value change.Revision
                        && previous.Stamp.Position < change.Stamp.Position
                        ->
                        return Some previous
                    | _ -> return corrupt ()
                | _ -> return corrupt ()
        }

    let position connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT sequence,entry_hash FROM claimcore.ledger_head WHERE singleton"
                    [||]
                    token

            match rows with
            | [ row ] ->
                let sequence =
                    DatabaseSession.value<int64> 0 row |> DatabaseSession.admitted Sequence.create

                let hash =
                    DatabaseSession.value<byte array> 1 row
                    |> EntryHash.ofBytes
                    |> Option.defaultWith corrupt

                return LedgerPosition.create sequence hash
            | _ -> return corrupt ()
        }
