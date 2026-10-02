namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Owner-only publication of a present, independently inspected external copy before a
/// case erasure request. The receipt never changes the copy's unresolved liability.
module internal ManagedCopyExternalPublicationOwner =
    let private validDocument (document: SignedCopyAdoptionDocument) =
        not (isNull (box document.Canonical))
        && document.Canonical.Length >= 2
        && document.Canonical.Length <= 16384
        && not (isNull (box document.Signature))
        && document.Signature.Length = 64

    let private existing ownerConnection publicationId ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            OwnerConnection.requireIdentity connection
            SchemaBaseline.requireCurrent connection
            use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

            let! row =
                ManagedCopyExternalPublicationRead.byPublication
                    connection
                    transaction
                    publicationId

            do! transaction.CommitAsync(ct)
            return row
        }

    let private audit ownerConnection witness commitments ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct
            return ()
        }

    let private same
        (submission: ExternalCopyPublicationSubmission)
        (row: StoredExternalCopyPublication)
        =
        row.PublicationId = submission.PublicationId
        && row.RegistryCanonical = submission.Registry.Canonical
        && row.RegistrySignature = submission.Registry.Signature
        && row.InspectionCanonical = submission.Inspection.Canonical
        && row.InspectionSignature = submission.Inspection.Signature
        && row.CandidateHash = SHA256.HashData(row.Canonical)

    let private replay
        ownerConnection
        (witness: WitnessProtocol)
        commitments
        (submission: ExternalCopyPublicationSubmission)
        (row: StoredExternalCopyPublication)
        ct
        =
        task {
            if not (same submission row) then
                return ExternalCopyPublicationOutcome.ResourceUnavailable
            else
                try
                    let intent =
                        witness.EvidenceStore.TryReadMetadataOperation(row.PublicationId, Intent)
                        |> Option.defaultWith (fun () -> raise WitnessPending)

                    if
                        intent.Ticket.Sequence <> row.WitnessSequence
                        || intent.Ticket.Epoch <> row.Epoch
                        || intent.Ticket.EntryHash <> row.WitnessHash
                        || intent.Ticket.SubjectCaseId <> Some row.CaseId
                    then
                        raise WitnessPending

                    if
                        witness.EvidenceStore
                            .TryReadMetadataOperation(row.PublicationId, SettledAuthority)
                            .IsNone
                    then
                        witness.ReconcileAuthority(
                            row.PublicationId,
                            row.WitnessSequence,
                            row.Epoch,
                            row.WitnessHash,
                            row.Canonical
                        )

                    use connection = new NpgsqlConnection(ownerConnection)
                    do! connection.OpenAsync(ct)
                    let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct

                    return
                        ExternalCopyPublicationOutcome.Published(
                            row.PublicationId,
                            row.WitnessSequence
                        )
                with _ ->
                    return ExternalCopyPublicationOutcome.Unconfirmed submission.PublicationId
        }

    let private commit
        connection
        transaction
        (witness: WitnessProtocol)
        (ready: ExternalCopyPublicationEvidence)
        =
        task {
            let canonical = ManagedCopyExternalPublicationCandidate.encode ready

            try
                try
                    let publicationId = ready.Registry.PublicationId

                    let intent =
                        witness.BeginAuthority(publicationId, canonical, Some ready.Registry.CaseId)

                    do!
                        ManagedCopyExternalPublicationWrite.insert
                            connection
                            transaction
                            ready
                            canonical
                            intent

                    do! transaction.CommitAsync(CancellationToken.None)
                    witness.SettleAuthority(publicationId, intent) |> ignore

                    witness.VerifyAuthorityEvidenceForCase(
                        publicationId,
                        intent.Ticket.Sequence,
                        intent.Ticket.Epoch,
                        intent.Ticket.EntryHash,
                        intent.CandidateHash,
                        ready.Registry.CaseId
                    )

                    return
                        ExternalCopyPublicationOutcome.Published(
                            publicationId,
                            intent.Ticket.Sequence
                        )
                with _ ->
                    return ExternalCopyPublicationOutcome.Unconfirmed ready.Registry.PublicationId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let private transact
        ownerConnection
        (witness: WitnessProtocol)
        (privateLocation: IExternalCopyPublicationPrivateLocation)
        (submission: ExternalCopyPublicationSubmission)
        ct
        =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            OwnerConnection.requireIdentity connection
            SchemaBaseline.requireCurrent connection
            use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)
            let! actorRevision = ActorGrantRead.lockRevision connection transaction true ct

            let! prepared =
                ManagedCopyExternalPublicationChecks.prepare
                    connection
                    transaction
                    witness
                    privateLocation
                    actorRevision
                    submission
                    ct

            match prepared with
            | Error refusal -> return refusal
            | Ok ready ->
                let! now = Sql.databaseNow connection transaction

                if
                    now >= ready.Registry.ValidUntil
                    || now >= ready.Inspection.ValidUntil
                    || now >= ready.PrivateLocationExpiresAt
                then
                    return ExternalCopyPublicationOutcome.ResourceUnavailable
                elif
                    witness.EvidenceStore
                        .TryReadMetadataOperation(submission.PublicationId, Intent)
                        .IsSome
                then
                    return ExternalCopyPublicationOutcome.Unconfirmed submission.PublicationId
                else
                    return! commit connection transaction witness ready
        }

    let publish
        (ownerConnection: string)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (privateLocation: IExternalCopyPublicationPrivateLocation)
        (submission: ExternalCopyPublicationSubmission)
        (ct: CancellationToken)
        : System.Threading.Tasks.Task<ExternalCopyPublicationOutcome> =
        task {
            if
                submission.PublicationId = Guid.Empty
                || not (validDocument submission.Registry)
                || not (validDocument submission.Inspection)
            then
                return ExternalCopyPublicationOutcome.ResourceUnavailable
            else
                try
                    witness.Admit()
                    commitments.Admit()
                    use fenceConnection = new NpgsqlConnection(ownerConnection)
                    do! fenceConnection.OpenAsync(ct)
                    OwnerConnection.requireIdentity fenceConnection
                    SchemaBaseline.requireCurrent fenceConnection

                    use! _authorityFence =
                        AuthorityOperationFence.acquireExclusive None fenceConnection ct

                    let! prior = existing ownerConnection submission.PublicationId ct

                    match prior with
                    | Some row ->
                        return! replay ownerConnection witness commitments submission row ct
                    | None ->
                        do! audit ownerConnection witness commitments ct

                        let! outcome =
                            transact ownerConnection witness privateLocation submission ct

                        match outcome with
                        | ExternalCopyPublicationOutcome.Published _ ->
                            try
                                do! audit ownerConnection witness commitments ct
                                return outcome
                            with _ ->
                                return
                                    ExternalCopyPublicationOutcome.Unconfirmed
                                        submission.PublicationId
                        | _ -> return outcome
                with _ ->
                    return ExternalCopyPublicationOutcome.AuditUnavailable "owner-admission"
        }
