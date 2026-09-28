namespace ClaimCore.Database

open System
open System.Threading
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness


/// Bounded keyset merge of the signed private registry against every current DB copy ID.
module internal DatabaseManagedCopyInventoryComparison =
    let private copyPage (connection: NpgsqlConnection) transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id,producer_kind,copy_kind,source_case_id,ciphertext_sha256,"
                    + "ciphertext_bytes,custodian_commitment,location_commitment,state "
                    + "FROM claimcore.managed_copies WHERE copy_id>@after "
                    + "ORDER BY copy_id LIMIT 50",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("after", after) |> ignore
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ManagedCopyLocationRow>()

            while reader.Read() do
                rows.Add
                    {
                        CopyId = reader.GetGuid(0)
                        ProducerKind = reader.GetString(1)
                        Kind = reader.GetString(2)
                        SourceCaseId = if reader.IsDBNull(3) then None else Some(reader.GetGuid(3))
                        CiphertextSha256 = reader.GetFieldValue<byte array>(4)
                        CiphertextBytes = reader.GetInt64(5)
                        CustodianCommitment =
                            if reader.IsDBNull(6) then
                                None
                            else
                                Some(reader.GetFieldValue<byte array>(6))
                        LocationCommitment =
                            if reader.IsDBNull(7) then
                                None
                            else
                                Some(reader.GetFieldValue<byte array>(7))
                        State = reader.GetString(8)
                    }

            return rows.ToArray()
        }


    let private observationMatches
        (observation: CopyObservation)
        (row: ManagedCopyLocationRow)
        absentPendingCopy
        adopted
        =
        let located = row.ProducerKind = "OWNER_ATTESTED" || adopted

        match observation.Status with
        | "PRESENT" when located ->
            observation.Sha256 = Some row.CiphertextSha256
            && observation.Bytes = Some row.CiphertextBytes
            && row.State <> "VERIFIED_DELETED"
        | "ABSENT" when located ->
            row.State = "VERIFIED_DELETED"
            || (absentPendingCopy = Some row.CopyId && row.State = "DELETE_PENDING")
        | "UNKNOWN" when located -> true
        | "UNKNOWN" -> row.ProducerKind = "PRODUCT_EXPORT" && row.State = "UNKNOWN"
        | _ -> false

    let private exactCopy
        key
        (entry: CopyLocationEntry)
        (observation: CopyObservation)
        (row: ManagedCopyLocationRow)
        absentPendingCopy
        origin
        =
        row.CopyId = entry.CopyId
        && row.CopyId = observation.CopyId
        && row.ProducerKind = entry.ProducerKind
        && row.Kind = entry.Kind
        && row.SourceCaseId = entry.SourceCaseId
        && row.CiphertextSha256 = entry.CiphertextSha256
        && row.CiphertextBytes = entry.CiphertextBytes
        && DatabaseManagedCopyCustodyComparison.matches key entry row origin
        && observationMatches observation row absentPendingCopy origin.IsSome

    let private verifiedOrigin
        connection
        transaction
        (witness: WitnessProtocol)
        cutoff
        (row: ManagedCopyLocationRow)
        (ct: CancellationToken)
        =
        task {
            match row.ProducerKind with
            | "PRODUCT_EXPORT"
            | "ADOPTED_EXTERNAL" ->
                let! found =
                    ManagedCopyAdoptionEvidence.verifyOrigin
                        connection
                        transaction
                        witness
                        cutoff
                        row.CopyId
                        ct

                match found with
                | Some origin ->
                    do!
                        DataAuditAdoptedCopyTransitions.verify
                            connection
                            transaction
                            witness
                            cutoff
                            origin
                            ct

                    return Some origin
                | None -> return None
            | _ -> return None
        }

    let private targetAbsent absentPendingCopy (observations: CopyObservation array) =
        absentPendingCopy
        |> Option.forall (fun id ->
            observations
            |> Array.exists (fun item -> item.CopyId = id && item.Status = "ABSENT"))

    let private checkPage
        connection
        transaction
        witness
        cutoff
        key
        (entries: CopyLocationEntry array)
        (observations: CopyObservation array)
        (absentPendingCopy: Guid option)
        requireAllAbsent
        startIndex
        (rows: ManagedCopyLocationRow array)
        ct
        =
        task {
            let mutable index = startIndex
            let mutable after = Guid.Empty
            let mutable valid = true

            for row in rows do
                if valid then
                    let! origin = verifiedOrigin connection transaction witness cutoff row ct

                    if
                        index >= entries.Length
                        || not (
                            exactCopy
                                key
                                entries[index]
                                observations[index]
                                row
                                absentPendingCopy
                                origin
                        )
                        || (requireAllAbsent && row.State <> "VERIFIED_DELETED")
                    then
                        valid <- false

                index <- index + 1
                after <- row.CopyId

            return index, after, valid && index <= 10000
        }

    let private eligible
        (evidence: SignedCopyLocationEvidence)
        (entries: CopyLocationEntry array)
        (observations: CopyObservation array)
        (absentPendingCopy: Guid option)
        requireAllAbsent
        =
        entries.Length = observations.Length
        && (not requireAllAbsent
            || (absentPendingCopy.IsNone
                && evidence.KnownUnmanaged.IsEmpty
                && (observations |> Array.forall (fun item -> item.Status = "ABSENT"))))

    let private completed valid absentPendingCopy observations index entryCount =
        if valid && targetAbsent absentPendingCopy observations && index = entryCount then
            Some index
        else
            None

    let verify
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        cutoff
        (key: ManagedCopyCommitmentKey)
        (evidence: SignedCopyLocationEvidence)
        (absentPendingCopy: Guid option)
        requireAllAbsent
        (ct: CancellationToken)
        =
        task {
            let entries = evidence.RegistryEntries |> List.sortBy _.CopyId |> List.toArray
            let observations = evidence.Observations |> List.sortBy _.CopyId |> List.toArray

            if not (eligible evidence entries observations absentPendingCopy requireAllAbsent) then
                return None
            else
                let mutable after = Guid.Empty
                let mutable index = 0
                let mutable complete = false
                let mutable valid = true

                while not complete && valid do
                    let! rows = copyPage connection transaction after ct
                    complete <- rows.Length < 50

                    let! nextIndex, nextAfter, pageValid =
                        checkPage
                            connection
                            transaction
                            witness
                            cutoff
                            key
                            entries
                            observations
                            absentPendingCopy
                            requireAllAbsent
                            index
                            rows
                            ct

                    index <- nextIndex
                    after <- nextAfter
                    valid <- pageValid

                return completed valid absentPendingCopy observations index entries.Length
        }
