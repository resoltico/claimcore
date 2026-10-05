namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal OwnerPruneApprovalRow =
    {
        Receipt: PruneApprovalReceipt
        ApprovedAt: DateTimeOffset
        ExpiresAt: DateTimeOffset
        Canonical: byte array
        StoredCandidateHash: byte array
        PurgeEventId: Guid
        CutoffSequence: int64
        CutoffHash: byte array
        TargetCount: int64
        TargetDigest: byte array
        AuthorityRevision: int64
        AuthorityHash: byte array
        ValidUntil: DateTimeOffset
    }

module internal CaseTombstonePruneOwnerApprovals =
    let private invalid () =
        raise (InvalidDataException("Witness prune approvals are incomplete."))

    let private sql =
        "SELECT approval_id,approver_actor_id,approver_grant_revision,approved_at,expires_at,"
        + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash,"
        + "purge_event_id,cutoff_sequence,cutoff_hash,target_count,target_digest,"
        + "expected_authority_revision,expected_authority_hash,valid_until "
        + "FROM claimcore.case_erasure_prune_approvals WHERE case_id=@case "
        + "AND prune_event_id=@event ORDER BY approver_actor_id LIMIT 3"

    let private row (reader: Data.Common.DbDataReader) =
        {
            Receipt =
                {
                    ApprovalId = reader.GetGuid(0)
                    ActorId = reader.GetGuid(1)
                    GrantRevision = reader.GetInt64(2)
                    CandidateHash = reader.GetFieldValue<byte array>(6)
                    WitnessSequence = reader.GetInt64(7)
                    WitnessEpoch = reader.GetInt64(8)
                    WitnessHash = reader.GetFieldValue<byte array>(9)
                }
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(3)
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(4)
            Canonical = reader.GetFieldValue<byte array>(5)
            StoredCandidateHash = reader.GetFieldValue<byte array>(6)
            PurgeEventId = reader.GetGuid(10)
            CutoffSequence = reader.GetInt64(11)
            CutoffHash = reader.GetFieldValue<byte array>(12)
            TargetCount = reader.GetInt64(13)
            TargetDigest = reader.GetFieldValue<byte array>(14)
            AuthorityRevision = reader.GetInt64(15)
            AuthorityHash = reader.GetFieldValue<byte array>(16)
            ValidUntil = reader.GetFieldValue<DateTimeOffset>(17)
        }

    let private load connection transaction (proposal: TombstonePruneProposal) =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" proposal.CaseId
            Sql.uuid command "event" proposal.EventId
            use! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<OwnerPruneApprovalRow>()

            while reader.Read() do
                rows.Add(row reader)

            return rows |> Seq.toList
        }

    let private principal connection transaction actorId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT principal_kind,issuer,principal_value FROM claimcore.actors "
                    + "WHERE actor_id=@actor",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actorId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                invalid ()

            let value =
                PrincipalKey.fromStorage
                    (reader.GetString(0))
                    (reader.GetString(1))
                    (reader.GetString(2))
                |> Result.defaultWith (fun _ -> invalid ())

            if reader.Read() then
                invalid ()

            return value
        }

    let private currentGrant connection transaction revision caseId (value: OwnerPruneApprovalRow) =
        task {
            let! key = principal connection transaction value.Receipt.ActorId

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    key
                    (ResourceScope.Case caseId)
                    revision
                    Threading.CancellationToken.None

            match authority with
            | None -> invalid ()
            | Some actor ->
                match
                    ActorAuthorization.authorizeAtRevision
                        key
                        actor
                        value.Receipt.GrantRevision
                        EndpointAction.ApproveWitnessPrune
                        (ResourceScope.Case caseId)
                with
                | AuthorizationDecision.Available(actorId, _) when actorId = value.Receipt.ActorId ->
                    ()
                | _ -> invalid ()
        }

    let private bound (proposal: TombstonePruneProposal) (value: OwnerPruneApprovalRow) =
        let digest text =
            CaseTombstonePruneApprovalPolicy.digest text |> Option.defaultWith invalid

        value.PurgeEventId = proposal.PurgeEventId
        && value.CutoffSequence = proposal.CutoffSequence
        && value.CutoffHash = digest proposal.CutoffHash
        && value.TargetCount = proposal.TargetCount
        && value.TargetDigest = digest proposal.TargetDigest
        && value.AuthorityRevision = proposal.ExpectedAuthorityRevision
        && value.AuthorityHash = digest proposal.ExpectedAuthorityHash
        && value.ValidUntil = proposal.ValidUntil

    let private validTime
        requireCurrent
        instant
        (proposal: TombstonePruneProposal)
        (value: OwnerPruneApprovalRow)
        =
        (not requireCurrent || value.ExpiresAt > instant)
        && value.ExpiresAt <= proposal.ValidUntil
        && value.ApprovedAt < value.ExpiresAt

    let private exact
        (witness: WitnessProtocol)
        (proposal: TombstonePruneProposal)
        requireCurrent
        instant
        (value: OwnerPruneApprovalRow)
        ct
        =
        task {
            let receipt = value.Receipt

            let canonical =
                CaseTombstoneCandidate.approval
                    proposal
                    receipt.ApprovalId
                    receipt.ActorId
                    receipt.GrantRevision
                    value.ApprovedAt
                    value.ExpiresAt

            try
                if
                    not (bound proposal value)
                    || not (validTime requireCurrent instant proposal value)
                    || value.Canonical <> canonical
                    || value.StoredCandidateHash <> SHA256.HashData(canonical)
                    || receipt.WitnessEpoch <> witness.Identity.Epoch
                then
                    invalid ()

                do!
                    witness.VerifyAuthorityEvidenceForCase(
                        receipt.ApprovalId,
                        receipt.WitnessSequence,
                        receipt.WitnessEpoch,
                        receipt.WitnessHash,
                        receipt.CandidateHash,
                        proposal.CaseId,
                        ct
                    )
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }

    let read
        connection
        transaction
        (witness: WitnessProtocol)
        revision
        (proposal: TombstonePruneProposal)
        requireCurrent
        instant
        ct
        =
        task {
            let! rows = load connection transaction proposal

            if rows.Length <> 2 || rows[0].Receipt.ActorId = rows[1].Receipt.ActorId then
                invalid ()

            for value in rows do
                do! exact witness proposal requireCurrent instant value ct

                if requireCurrent then
                    do! currentGrant connection transaction revision proposal.CaseId value

            return rows |> List.map _.Receipt
        }
