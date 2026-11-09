namespace ClaimCore.Postgres

open System
open System.Text
open ClaimCore.Domain
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
        | CompartmentOwner.Case id -> "CASE", CaseId.value id
        | CompartmentOwner.ActorProfile id -> "ACTOR_PROFILE", ActorId.value id

    let private purpose =
        function
        | CompartmentPurpose.Case -> "CASE"
        | CompartmentPurpose.Work -> "WORK"
        | CompartmentPurpose.Note -> "NOTE"
        | CompartmentPurpose.Reminder -> "REMINDER"
        | CompartmentPurpose.Profile -> "PROFILE"

    let parameters (root: OperationRoot) =
        let header = root.Header

        let effects =
            OperationRoot.effectBytes root
            |> List.map (fun (index, bytes) ->
                JsonValue.Object
                    [ "index", JsonValue.Integer(int64 index); "descriptor", json bytes ])
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
                        "ownerKind", JsonValue.String kind
                        "ownerId", JsonValue.String(identity.ToString("D"))
                        "purpose", JsonValue.String(purpose descriptor.Purpose)
                        "effects",
                        JsonValue.Array(
                            descriptor.Effects |> List.map (int64 >> JsonValue.Integer)
                        )
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
            DatabaseSession.parameter header.BusinessDate
            box (Encoding.UTF8.GetString(OperationRoot.commandBytes root))
            box effects
            box compartments
        |]

    let append connection root token =
        DatabaseSession.execute
            connection
            "SELECT claimcore.append_operation($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb,$11::jsonb,$12::jsonb)"
            (parameters root)
            token

    let private corrupt () =
        raise (RecordCorruptException "Invalid retained operation structure or contents.")

    let private admitted result =
        Result.defaultWith (fun _ -> corrupt ()) result

    let private compartment installation operation row =
        let owner =
            let id = DatabaseSession.value<Guid> 2 row

            match DatabaseSession.value<string> 1 row with
            | "CASE" -> CompartmentOwner.Case(DatabaseSession.admitted CaseId.create id)
            | "ACTOR_PROFILE" ->
                CompartmentOwner.ActorProfile(DatabaseSession.admitted ActorId.create id)
            | _ -> corrupt ()

        let purpose =
            match DatabaseSession.value<string> 3 row with
            | "CASE" -> CompartmentPurpose.Case
            | "WORK" -> CompartmentPurpose.Work
            | "NOTE" -> CompartmentPurpose.Note
            | "REMINDER" -> CompartmentPurpose.Reminder
            | "PROFILE" -> CompartmentPurpose.Profile
            | _ -> corrupt ()

        let descriptor =
            CompartmentDescriptor.create
                (DatabaseSession.value<int> 0 row)
                owner
                purpose
                (DatabaseSession.value<int array> 4 row |> Array.toList)
            |> admitted

        Compartment.restore
            installation
            operation
            descriptor
            (DatabaseSession.value<byte array> 5 row)
            (DatabaseSession.optional<byte array> 6 row)
            (DatabaseSession.optional<byte array> 7 row)
        |> admitted

    let find connection installation operation token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT sequence,previous_hash,entry_hash,installation_format,actor_id,recorded_at,business_date,command::text FROM claimcore.ledger WHERE operation_id=$1"
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
                        BusinessDate = DatabaseSession.optional<DateOnly> 6 row
                    }

                let! effectRows =
                    DatabaseSession.rows
                        connection
                        "SELECT effect_index,descriptor::text FROM claimcore.ledger_effects WHERE operation_sequence=$1 ORDER BY effect_index"
                        [| box (Sequence.value header.Sequence) |]
                        token

                let! contentRows =
                    DatabaseSession.rows
                        connection
                        "SELECT ordinal,owner_kind,owner_id,purpose,effect_indices,commitment,nonce,payload FROM claimcore.ledger_compartments WHERE operation_sequence=$1 ORDER BY ordinal"
                        [| box (Sequence.value header.Sequence) |]
                        token

                let effects =
                    effectRows
                    |> List.map (fun row ->
                        DatabaseSession.value<int> 0 row,
                        Encoding.UTF8.GetBytes(DatabaseSession.value<string> 1 row))

                let contents = contentRows |> List.map (compartment installation operation)

                let hash =
                    DatabaseSession.value<byte array> 2 row
                    |> EntryHash.ofBytes
                    |> Option.defaultWith corrupt

                let root =
                    OperationRoot.restore
                        header
                        (Encoding.UTF8.GetBytes(DatabaseSession.value<string> 7 row))
                        effects
                        contents
                        hash
                    |> admitted

                return Some(RetainedOperation.decode root |> admitted)
            | _ -> return corrupt ()
        }
