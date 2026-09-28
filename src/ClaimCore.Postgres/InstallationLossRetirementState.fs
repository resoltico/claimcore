namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal InstallationLossRetirementOutcome =
    | AwaitingPrimary of retirementId: Guid * sequence: int64 * hash: byte array
    | AwaitingWitness of retirementId: Guid * sequence: int64 * hash: byte array
    | Retired of retirementId: Guid * sequence: int64 * hash: byte array
    | Refused
    | Unconfirmed of retirementId: Guid

/// Exact live authority snapshot shared by candidate creation and terminal execution.
module internal InstallationLossRetirementState =
    let primaryIdentity (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,loss_retired "
                + "FROM claimcore.installation_lineage WHERE singleton FOR UPDATE",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary installation identity is unavailable."

        let identity =
            {
                InstallationId = reader.GetGuid(0)
                LineageId = reader.GetGuid(1)
                Epoch = reader.GetInt64(2)
            }

        let retired = reader.GetBoolean(3)

        if reader.Read() then
            invalidOp "Primary installation identity is duplicated."

        identity, retired

    let authorityRevision (connection: NpgsqlConnection) transaction =
        use command =
            new NpgsqlCommand(
                "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                connection,
                transaction
            )

        match command.ExecuteScalar() with
        | :? int64 as revision when revision > 0L -> revision
        | _ -> invalidOp "Loss decision owner authority is unavailable."

    let databaseNow primaryOwner transaction =
        ManagedCopySignerPolicy.databaseNow primaryOwner transaction
        |> fun work -> work.GetAwaiter().GetResult()

    let verifiedOwnerAuthority primaryOwner transaction witness cutoff =
        let projection =
            DataAuditWitness.verifyAuthorityEvents
                primaryOwner
                transaction
                witness
                cutoff
                CancellationToken.None
            |> fun work -> work.GetAwaiter().GetResult()

        DataAuditAuthorityProjection.verify
            primaryOwner
            transaction
            projection
            CancellationToken.None
        |> fun work -> work.GetAwaiter().GetResult()
        |> ignore

        projection.Revision

    let matchingIdentity (identity: Identity) (witness: WitnessProtocol) =
        identity = witness.Identity

    let expectedEvidence (value: InstallationLossRetirementDecision) evidence checkpoint =
        InstallationLossRetirementCandidate.evidenceDigest evidence = value.EvidenceReportSha256
        && InstallationLossRetirementCandidate.evidenceDigest checkpoint =
            value.IndependentCheckpointSha256

    let committedWitness
        (witness: WitnessProtocol)
        (value: InstallationLossRetirementDecision)
        (settlement: Ticket)
        =
        let snapshot = witness.Snapshot()

        snapshot.LossRetired
        && not snapshot.LossRetirementPending
        && snapshot.LossRetirementId = Some value.RetirementId
        && snapshot.LossRetirementSequence = Some settlement.Sequence
        && snapshot.LossRetirementHash = Some settlement.EntryHash
