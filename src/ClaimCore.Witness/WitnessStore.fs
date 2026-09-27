namespace ClaimCore.Witness

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes

type Store private (writerConnection: string, identity: Identity, material: byte array option) =
    let writerConnection = PostgresTransport.connectionString writerConnection

    let capability =
        material
        |> Option.map (fun writerCapability ->
            if
                isNull (box writerCapability)
                || writerCapability.Length <> 32
                || not (writerCapability |> Array.exists ((<>) 0uy))
            then
                invalidArg (nameof writerCapability) "Witness writer capability is invalid."

            Array.copy writerCapability)

    let mutable disposed = false

    let conn () =
        PostgresTransport.connection writerConnection

    let checkAdmission = WitnessStoreRead.checkAdmission identity
    let readEvidence = WitnessStoreRead.readEvidence identity

    new(writerConnection: string, identity: Identity, writerCapability: byte array) =
        new Store(writerConnection, identity, Some writerCapability)

    static member internal OpenAudit(auditConnection: string, identity: Identity) =
        new Store(auditConnection, identity, None)

    member internal _.WithWriterCapability<'value>(action: byte array -> Task<'value>) =
        if disposed then
            invalidOp "Witness writer capability is unavailable."

        let privateCopy =
            capability
            |> Option.map Array.copy
            |> Option.defaultWith (fun () -> invalidOp "Auditor cannot use writer capability.")

        task {
            try
                return! action privateCopy
            finally
                CryptographicOperations.ZeroMemory(privateCopy)
        }

    /// The payload must already be an authenticated encryption envelope from the key custodian.
    /// This adapter stores opaque bytes and does not hold or derive an encryption key.
    member _.Append
        (
            operation: Guid,
            subjectCaseId: Guid option,
            phase: Phase,
            keyId: Guid,
            encryptedPayload: byte array
        ) =
        let active =
            capability
            |> Option.defaultWith (fun () -> invalidOp "Auditor cannot append witness events.")

        WitnessStoreAppend.append
            writerConnection
            identity
            active
            operation
            subjectCaseId
            phase
            keyId
            encryptedPayload

    member _.TryReadEvidence(operation: Guid, phase: Phase) =
        use connection = conn ()
        connection.Open()
        checkAdmission connection
        readEvidence connection operation phase

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                capability |> Option.iter CryptographicOperations.ZeroMemory

    member this.Read(operation: Guid, phase: Phase) =
        this.TryReadEvidence(operation, phase)
        |> Option.map _.Ticket
        |> Option.defaultWith (fun () -> invalidOp "Witness committed row is missing.")

    member _.Admit() =
        use connection = conn ()
        connection.Open()
        checkAdmission connection

        let active =
            capability
            |> Option.defaultWith (fun () -> invalidOp "Auditor cannot admit case work.")

        WitnessStoreRead.checkWriterAdmission identity active connection |> ignore

    member _.AdmitReadOnly() =
        use connection = conn ()
        connection.Open()
        checkAdmission connection

    /// Holds a witness row-share lock for the duration of a claimant-bearing read. A handoff
    /// requires the exclusive tip lock and therefore cannot pass this read's linearization point.
    member _.AcquireReadFence(expectedGeneration: int64) =
        if expectedGeneration < 1L || disposed then
            invalidOp "Witness read fence is unavailable."

        let active =
            capability
            |> Option.defaultWith (fun () -> invalidOp "Auditor cannot acquire a writer lease.")

        WitnessStoreReadLease.acquire writerConnection identity active expectedGeneration

    member _.ReadKeyCheck() =
        use connection = conn ()
        connection.Open()
        checkAdmission connection

        use command =
            new NpgsqlCommand(
                "SELECT active_key_id,key_check_envelope FROM claimcore_witness.installation "
                + "WHERE singleton AND installation_id=@installation AND lineage_id=@lineage",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Witness key marker is missing."

        let result = reader.GetGuid(0), reader.GetFieldValue<byte array>(1)

        if reader.Read() then
            invalidOp "Witness key marker is duplicated."

        result

    member _.RequiredKeyIds() =
        use connection = conn ()
        connection.Open()
        checkAdmission connection

        use command =
            new NpgsqlCommand(
                "SELECT DISTINCT key_id FROM claimcore_witness.journal "
                + "WHERE installation_id=@installation LIMIT 65",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        use reader = command.ExecuteReader()
        let ids = ResizeArray<Guid>()

        while reader.Read() do
            ids.Add(reader.GetGuid(0))

        if ids.Count > 64 then
            invalidOp "Witness key rotation limit is exceeded."

        ids |> Seq.toList

    member _.Snapshot() =
        WitnessStoreSnapshot.read writerConnection identity

    member internal _.ReadDataUseActivation() =
        WitnessDataUseActivation.read writerConnection identity

    /// Historical checkpoint lookup is deliberately not a full journal-chain audit.
    member _.TryReadEntryHash(sequence: int64) =
        WitnessStoreRead.tryReadEntryHash writerConnection identity sequence

    member _.ReadPage
        (afterSequence: int64, expectedPreviousHash: byte array, cutoffSequence: int64, limit: int)
        : JournalPage =
        WitnessStorePayloadPage.read
            writerConnection
            identity
            afterSequence
            expectedPreviousHash
            cutoffSequence
            limit

    /// The global immutable chain is verified even when authorized CASE ciphertext is absent.
    /// PayloadPresent is evidence state, not a deletion authorization; auditors must prove the
    /// exact witnessed prune receipt before tolerating false.
    member _.ReadMetadataPage
        (afterSequence: int64, expectedPreviousHash: byte array, cutoffSequence: int64, limit: int)
        : MetadataPage =
        WitnessStoreMetadata.readPage
            writerConnection
            identity
            afterSequence
            expectedPreviousHash
            cutoffSequence
            limit

    /// Local exact ticket/hash read; full-audit callers must still verify the whole chain.
    member _.TryReadMetadataAtSequence(sequence: int64) =
        WitnessStoreMetadata.readAt writerConnection identity sequence

    member _.TryReadMetadataOperation(operation: Guid, phase: Phase) =
        WitnessStoreMetadata.readOperation writerConnection identity operation phase

    member internal _.TryReadHandoff(handoffId: Guid) =
        WitnessStoreHandoff.read writerConnection identity handoffId

    /// Emits tentative bounded pages of case INTENT operations. The caller must not commit
    /// dependent mutations until the complete global chain and cutoff have been verified.
    member _.ReadSubjectOperations
        (subjectCaseId: Guid, cutoffSequence: int64, emitPage: SubjectOperation list -> unit)
        =
        WitnessSubjectOperations.scan
            writerConnection
            identity
            (Some subjectCaseId)
            cutoffSequence
            emitPage

    /// Verifies every global metadata link through the requested sequence, including rows whose
    /// ciphertext has been authorized for pruning. A missing/forked row fails rather than returning None.
    member this.TryReadVerifiedEntryHash(sequence: int64) =
        if sequence < 0L then
            invalidArg (nameof sequence) "Witness sequence is invalid."

        if sequence > (this.Snapshot()).TipSequence then
            None
        else
            WitnessSubjectOperations.scan writerConnection identity None sequence ignore
            |> fun result -> Some result.CutoffHash

    member _.TryReadTipEvidence() =
        use connection = conn ()
        connection.Open()
        checkAdmission connection
        WitnessStoreRead.readTipEvidence identity connection
