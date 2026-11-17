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

    let parameters (root: OperationRoot) =
        let header = root.Header

        let effects =
            OperationRoot.effectBytes root
            |> List.map (snd >> json)
            |> JsonValue.Array
            |> text

        let compartments =
            root.Structure.Compartments
            |> List.map (fun compartment ->
                let descriptor = compartment.Descriptor
                let kind, identity = owner descriptor.Owner

                let nonce, payload =
                    match Compartment.content compartment with
                    | Some(nonce, payload) ->
                        JsonValue.String(Convert.ToHexStringLower nonce),
                        JsonValue.String(Convert.ToHexStringLower payload)
                    | None -> JsonValue.Null, JsonValue.Null

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
            box (ActorId.value header.ActorId)
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

    let append connection root token =
        DatabaseSession.execute
            connection
            "SELECT claimcore.append_operation($1,$2,$3,$4,$5,$6,$7,$8,$9::text::date,$10,$11,$12::jsonb,$13::jsonb,$14::jsonb)"
            (parameters root)
            token

    let private corrupt () =
        raise (RecordCorruptException "Invalid retained operation structure or contents.")

    let private admitted result =
        Result.defaultWith (fun _ -> corrupt ()) result

    let private compartment row =
        let identity = DatabaseSession.value<Guid> 2 row

        let owner =
            match DatabaseSession.value<string> 1 row with
            | "FAMILY" -> CompartmentOwner.Family(DatabaseSession.admitted CaseId.create identity)
            | "PROFILE" ->
                CompartmentOwner.Profile(DatabaseSession.admitted ActorId.create identity)
            | _ -> corrupt ()

        let descriptor =
            CompartmentDescriptor.create (DatabaseSession.value<int> 0 row) owner
            |> admitted

        Compartment.restore
            descriptor
            (DatabaseSession.value<byte array> 3 row)
            (DatabaseSession.optional<byte array> 4 row)
            (DatabaseSession.optional<byte array> 5 row)
        |> admitted

    let private read includePrivate connection installation operation token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ("SELECT sequence,previous_hash,entry_hash,installation_format,actor_id,recorded_at,"
                     + DatabaseSession.dateExpression "business_date"
                     + ",structure::text,task FROM claimcore.ledger WHERE operation_id=$1")
                    [| box (OperationId.value operation) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                if DatabaseSession.value<int16> 3 row <> int16 InstallationFormat.Current then
                    corrupt ()

                let header =
                    {
                        OperationHeader.InstallationId = installation
                        Sequence =
                            DatabaseSession.value<int64> 0 row
                            |> DatabaseSession.admitted Sequence.create
                        PreviousHash =
                            DatabaseSession.value<byte array> 1 row
                            |> EntryHash.ofBytes
                            |> Option.defaultWith corrupt
                        OperationId = operation
                        ActorId =
                            DatabaseSession.value<Guid> 4 row
                            |> DatabaseSession.admitted ActorId.create
                        RecordedAt = DatabaseSession.instant 5 row
                        BusinessDate = DatabaseSession.optionalCalendarDate 6 row
                    }

                let! effectRows =
                    DatabaseSession.rows
                        connection
                        "SELECT effect_index,(jsonb_build_object('aggregate',jsonb_build_object('kind',aggregate_kind,'id',aggregate_id),'part',jsonb_build_object('kind',part_kind,'id',part_id),'revision',revision,'naturalKey',natural_key,'retained',retained,'erases',CASE WHEN erases_kind IS NOT NULL THEN jsonb_build_object('kind',erases_kind,'id',erases_id) END,'compartment',compartment) - array_remove(ARRAY[CASE WHEN natural_key IS NULL THEN 'naturalKey' END,CASE WHEN retained IS NULL THEN 'retained' END,CASE WHEN erases_kind IS NULL THEN 'erases' END,CASE WHEN compartment IS NULL THEN 'compartment' END],NULL))::text FROM claimcore.ledger_effects WHERE operation_sequence=$1 ORDER BY effect_index"
                        [| box (Sequence.value header.Sequence) |]
                        token

                let! contentRows =
                    DatabaseSession.rows
                        connection
                        (if includePrivate then
                             "SELECT ordinal,owner_kind,owner_id,commitment,nonce,payload FROM claimcore.ledger_compartments WHERE operation_sequence=$1 ORDER BY ordinal"
                         else
                             "SELECT ordinal,owner_kind,owner_id,commitment,NULL::bytea,NULL::bytea FROM claimcore.ledger_compartments WHERE operation_sequence=$1 ORDER BY ordinal")
                        [| box (Sequence.value header.Sequence) |]
                        token

                let effects =
                    effectRows
                    |> List.map (fun row ->
                        DatabaseSession.value<int> 0 row,
                        Encoding.UTF8.GetBytes(DatabaseSession.value<string> 1 row))

                let contents = contentRows |> List.map compartment

                let hash =
                    DatabaseSession.value<byte array> 2 row
                    |> EntryHash.ofBytes
                    |> Option.defaultWith corrupt

                let root =
                    OperationRoot.restore
                        header
                        (DatabaseSession.value<string> 8 row)
                        (Encoding.UTF8.GetBytes(DatabaseSession.value<string> 7 row))
                        effects
                        contents
                        hash
                    |> admitted

                return Some(RetainedOperation.restore root |> admitted)
            | _ -> return corrupt ()
        }

    let find connection installation operation token =
        read true connection installation operation token

    /// Commitments authenticate public structure without acquiring private compartment bytes.
    let findStructure connection installation operation token =
        read false connection installation operation token

    let previous connection installation (change: PartChange) token =
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

                    let! record = find connection installation operation token

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
