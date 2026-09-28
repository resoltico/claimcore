namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal PublishedInstallationUsePlan =
    {
        PlanId: Guid
        ActivationId: Guid
        Plan: BackupHealthActivationPlan
        IntentSequence: int64
        IntentHash: byte array
        SettlementSequence: int64
        SettlementHash: byte array
        PublishedAt: DateTimeOffset
    }

/// Shared read-only publication proof for actor review, approvals, owner activation and audit.
module internal InstallationUsePlanRead =
    [<NoEquality; NoComparison>]
    type private PlanRow =
        {
            ActivationId: Guid
            InstallationId: Guid
            LineageId: Guid
            Epoch: int64
            WriterGeneration: int64
            PolicySha256: byte array
            PlanSha256: byte array
            CanonicalPlan: byte array
            PublishedAt: DateTimeOffset
            IntentSequence: int64
            IntentEpoch: int64
            IntentHash: byte array
            Extra: bool
        }

    let private readRow connection transaction planId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT activation_id,installation_id,lineage_id,witness_epoch,writer_generation,"
                    + "policy_sha256,plan_sha256,canonical_plan,published_at,"
                    + "witness_intent_sequence,witness_epoch_at_publication,witness_intent_hash "
                    + "FROM claimcore.installation_data_use_plans WHERE plan_id=@plan",
                    connection,
                    transaction
                )

            Sql.uuid command "plan" planId
            use! reader = command.ExecuteReaderAsync(ct)
            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let row =
                    {
                        ActivationId = reader.GetGuid(0)
                        InstallationId = reader.GetGuid(1)
                        LineageId = reader.GetGuid(2)
                        Epoch = reader.GetInt64(3)
                        WriterGeneration = reader.GetInt64(4)
                        PolicySha256 = reader.GetFieldValue<byte array>(5)
                        PlanSha256 = reader.GetFieldValue<byte array>(6)
                        CanonicalPlan = reader.GetFieldValue<byte array>(7)
                        PublishedAt = reader.GetFieldValue<DateTimeOffset>(8)
                        IntentSequence = reader.GetInt64(9)
                        IntentEpoch = reader.GetInt64(10)
                        IntentHash = reader.GetFieldValue<byte array>(11)
                        Extra = false
                    }

                let! extra = reader.ReadAsync(ct)
                return Some { row with Extra = extra }
        }

    let private validateRow (witness: WitnessProtocol) planId (row: PlanRow) =
        let plan =
            InstallationUsePlanCodec.parse row.CanonicalPlan
            |> Option.defaultWith (fun () -> invalidOp "Published activation plan is invalid.")

        let identityMatches =
            row.InstallationId = witness.Identity.InstallationId
            && row.LineageId = witness.Identity.LineageId
            && row.Epoch = witness.Identity.Epoch
            && row.IntentEpoch = witness.Identity.Epoch
            && plan.InstallationId = row.InstallationId
            && plan.LineageId = row.LineageId
            && plan.Epoch = row.Epoch
            && plan.WriterGeneration = row.WriterGeneration

        let contentMatches =
            row.CanonicalPlan.Length <= 16384
            && row.PlanSha256 = SHA256.HashData(row.CanonicalPlan)
            && row.PolicySha256 = Convert.FromHexString(plan.PolicySha256)
            && row.PlanSha256 = Convert.FromHexString(plan.PlanSha256)

        let expectedPlanId =
            InstallationUseActivationCandidate.planIdFromDigest row.InstallationId row.PlanSha256

        let expectedActivationId =
            InstallationUseActivationCandidate.eventIdFromPlan row.InstallationId row.PlanSha256

        let idsMatch = planId = expectedPlanId && row.ActivationId = expectedActivationId

        if row.Extra || not identityMatches || not contentMatches || not idsMatch then
            invalidOp "Published activation plan diverged."

        plan

    let verified
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        planId
        (ct: CancellationToken)
        =
        task {
            match! readRow connection transaction planId ct with
            | None -> return None
            | Some row ->
                let plan = validateRow witness planId row

                let settled =
                    witness.EvidenceStore.TryReadEvidence(planId, SettledAuthority)
                    |> Option.defaultWith (fun () ->
                        invalidOp "Published plan is not witnessed settled.")

                if settled.Ticket.Sequence <= row.IntentSequence then
                    invalidOp "Published plan settlement order is invalid."

                WriterActivationWitness.verifyHistorical
                    witness
                    planId
                    row.CanonicalPlan
                    (row.IntentSequence, row.IntentHash)
                    (settled.Ticket.Sequence, settled.Ticket.EntryHash)
                |> ignore

                return
                    Some
                        {
                            PlanId = planId
                            ActivationId = row.ActivationId
                            Plan = plan
                            IntentSequence = row.IntentSequence
                            IntentHash = row.IntentHash
                            SettlementSequence = settled.Ticket.Sequence
                            SettlementHash = settled.Ticket.EntryHash
                            PublishedAt = row.PublishedAt
                        }
        }
