namespace ClaimCore.Witness

open System
open System.Security.Cryptography
open System.Threading
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

    // Long-held read fences use a separate bounded pool so their nested witness
    // snapshots and evidence reads cannot exhaust the same connection pool.
    let readFenceSource =
        capability
        |> Option.map (fun _ ->
            try
                let builder = NpgsqlConnectionStringBuilder(writerConnection)
                builder.MaxPoolSize <- min builder.MaxPoolSize 32
                builder.MinPoolSize <- 0
                NpgsqlDataSource.Create(builder.ConnectionString)
            with _ ->
                capability |> Option.iter CryptographicOperations.ZeroMemory
                reraise ())

    let ownership = obj ()
    let mutable disposed = false

    let conn () =
        PostgresTransport.connection writerConnection

    let checkAdmission = WitnessDatabaseAdmission.checkAsync identity
    let readEvidence = WitnessStoreRead.readEvidence identity

    new(writerConnection: string, identity: Identity, writerCapability: byte array) =
        new Store(writerConnection, identity, Some writerCapability)

    static member internal OpenAudit(auditConnection: string, identity: Identity) =
        new Store(auditConnection, identity, None)

    member internal _.WithWriterCapability<'value>(action: byte array -> Task<'value>) =
        let privateCopy =
            lock ownership (fun () ->
                if disposed then
                    invalidOp "Witness writer capability is unavailable."

                capability
                |> Option.map Array.copy
                |> Option.defaultWith (fun () -> invalidOp "Auditor cannot use writer capability."))

        task {
            try
                return! action privateCopy
            finally
                CryptographicOperations.ZeroMemory(privateCopy)
        }

    /// The payload must already be an authenticated encryption envelope from the key custodian.
    /// This adapter stores opaque bytes and does not hold or derive an encryption key.
    member this.Append
        (
            operation: Guid,
            subjectCaseId: Guid option,
            phase: Phase,
            keyId: Guid,
            encryptedPayload: byte array,
            ct: CancellationToken
        ) =
        this.WithWriterCapability(fun active ->
            WitnessStoreAppend.append
                writerConnection
                identity
                active
                operation
                subjectCaseId
                phase
                keyId
                encryptedPayload
                ct)

    member _.TryReadEvidence(operation: Guid, phase: Phase, ct: CancellationToken) =
        task {
            use connection = conn ()
            do! connection.OpenAsync(ct)
            do! checkAdmission connection ct
            return! readEvidence connection operation phase ct
        }

    member internal _.TryReadLossRetirement(retirementId: Guid, ct: CancellationToken) =
        WitnessStoreLossRetirement.read writerConnection identity retirementId ct

    interface IDisposable with
        member _.Dispose() =
            let close =
                lock ownership (fun () ->
                    if disposed then
                        false
                    else
                        disposed <- true
                        capability |> Option.iter CryptographicOperations.ZeroMemory
                        true)

            if close then
                readFenceSource |> Option.iter (fun source -> source.Dispose())

    member this.Read(operation: Guid, phase: Phase, ct: CancellationToken) =
        task {
            let! evidence = this.TryReadEvidence(operation, phase, ct)

            return
                evidence
                |> Option.map _.Ticket
                |> Option.defaultWith (fun () -> invalidOp "Witness committed row is missing.")
        }

    member this.Admit(ct: CancellationToken) =
        this.WithWriterCapability(fun active ->
            task {
                use connection = conn ()
                do! connection.OpenAsync(ct)
                do! checkAdmission connection ct
                let! _ = WitnessStoreRead.checkWriterAdmission identity active connection ct
                return ()
            })

    member _.AdmitReadOnly(ct: CancellationToken) =
        task {
            use connection = conn ()
            do! connection.OpenAsync(ct)
            do! checkAdmission connection ct
        }

    /// Holds a witness row-share lock for the duration of a claimant-bearing read. A handoff
    /// requires the exclusive tip lock and therefore cannot pass this read's linearization point.
    member this.AcquireReadFence(expectedGeneration: int64, ct: CancellationToken) =
        if expectedGeneration < 1L then
            invalidOp "Witness read fence is unavailable."

        this.WithWriterCapability(fun active ->
            let source =
                readFenceSource
                |> Option.defaultWith (fun () -> invalidOp "Witness read fence is unavailable.")

            WitnessStoreReadLease.acquire source identity active expectedGeneration ct)

    member _.ReadKeyCheck(ct: CancellationToken) =
        WitnessStoreKeyRequirements.readCheck writerConnection identity ct

    member _.RequiredKeyIds(ct: CancellationToken) =
        WitnessStoreKeyRequirements.requiredIds writerConnection identity ct

    member _.Snapshot(ct: CancellationToken) =
        WitnessStoreSnapshot.read writerConnection identity ct

    member internal _.ReadDataUseActivation(ct: CancellationToken) =
        WitnessDataUseActivation.read writerConnection identity ct

    /// Historical checkpoint lookup is deliberately not a full journal-chain audit.
    member _.TryReadEntryHash(sequence: int64, ct: CancellationToken) =
        WitnessStoreRead.tryReadEntryHash writerConnection identity sequence ct

    member _.ReadPage
        (
            afterSequence: int64,
            expectedPreviousHash: byte array,
            cutoffSequence: int64,
            limit: int,
            ct: CancellationToken
        ) : Task<JournalPage> =
        WitnessStorePayloadPage.read
            writerConnection
            identity
            afterSequence
            expectedPreviousHash
            cutoffSequence
            limit
            ct

    /// The global immutable chain is verified even when authorized CASE ciphertext is absent.
    /// PayloadPresent is evidence state, not a deletion authorization; auditors must prove the
    /// exact witnessed prune receipt before tolerating false.
    member _.ReadMetadataPage
        (
            afterSequence: int64,
            expectedPreviousHash: byte array,
            cutoffSequence: int64,
            limit: int,
            ct: CancellationToken
        ) : Task<MetadataPage> =
        WitnessStoreMetadata.readPage
            writerConnection
            identity
            afterSequence
            expectedPreviousHash
            cutoffSequence
            limit
            ct

    /// Local exact ticket/hash read; full-audit callers must still verify the whole chain.
    member _.TryReadMetadataAtSequence(sequence: int64, ct: CancellationToken) =
        WitnessStoreMetadata.readAt writerConnection identity sequence ct

    member _.TryReadMetadataOperation(operation: Guid, phase: Phase, ct: CancellationToken) =
        WitnessStoreMetadata.readOperation writerConnection identity operation phase ct

    member internal _.TryReadHandoff(handoffId: Guid, ct: CancellationToken) =
        WitnessStoreHandoff.read writerConnection identity handoffId ct

    /// Emits tentative bounded pages of case INTENT operations. The caller must not commit
    /// dependent mutations until the complete global chain and cutoff have been verified.
    member _.ReadSubjectOperations
        (
            subjectCaseId: Guid,
            cutoffSequence: int64,
            emitPage: SubjectOperation list -> Task<unit>,
            ct: CancellationToken
        ) =
        WitnessSubjectOperations.scan
            writerConnection
            identity
            (Some subjectCaseId)
            cutoffSequence
            emitPage
            ct

    /// Verifies every global metadata link through the requested sequence, including rows whose
    /// ciphertext has been authorized for pruning. A missing/forked row fails rather than returning None.
    member this.TryReadVerifiedEntryHash(sequence: int64, ct: CancellationToken) =
        task {
            if sequence < 0L then
                invalidArg (nameof sequence) "Witness sequence is invalid."

            let! tip = this.Snapshot(ct)

            if sequence > tip.TipSequence then
                return None
            else
                let! result =
                    WitnessSubjectOperations.scan
                        writerConnection
                        identity
                        None
                        sequence
                        (fun _ -> Task.FromResult())
                        ct

                return Some result.CutoffHash
        }

    member _.TryReadTipEvidence(ct: CancellationToken) =
        task {
            use connection = conn ()
            do! connection.OpenAsync(ct)
            do! checkAdmission connection ct
            return! WitnessStoreRead.readTipEvidence identity connection ct
        }
