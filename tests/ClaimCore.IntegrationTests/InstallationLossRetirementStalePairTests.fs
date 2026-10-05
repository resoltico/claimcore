module ClaimCore.IntegrationTests.InstallationLossRetirementStalePairTests

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.InstallationLossRetirementFixture
open ClaimCore.IntegrationTests.InstallationLossRetirementCrashFixture

let private execute (connection: NpgsqlConnection) transaction sql =
    use command = new NpgsqlCommand(sql, connection, transaction)
    command.ExecuteNonQuery() |> ignore

let private closed (context: Context) reference artifact =
    Expect.throwsT<InvalidOperationException>
        (fun () ->
            (context.Runtime.ForActor(human "loss-owner-first"))
                .Get(reference, CancellationToken.None)
            |> await
            |> ignore)
        "Surviving terminal evidence prevents ordinary accepted-case disclosure."

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            (context.Runtime.ForActor(human "loss-owner-first"))
                .Recovery.PreviewEnvelopeImport(artifact, CancellationToken.None)
            |> await
            |> ignore)
        "Old signed artifact cannot be imported or called failed after retirement."

let private auditRejected (context: Context) =
    use audit = new NpgsqlConnection(context.OwnerConnectionString)
    audit.Open()

    Expect.throwsT<InvalidDataException>
        (fun () -> DataAudit.run audit context.Witness CancellationToken.None |> await |> ignore)
        "Full audit detects a one-cluster rollback against surviving authority."

let private primaryRollback (context: Context) (decision: Decision) caseReference artifact =
    execute
        context.Primary
        null
        ("UPDATE claimcore.installation_lineage SET loss_retired=false,"
         + "loss_retirement_id=NULL,loss_retirement_intent_sequence=NULL,"
         + "loss_retirement_intent_hash=NULL WHERE singleton")

    try
        auditRejected context
        closed context caseReference artifact
    finally
        use command =
            new NpgsqlCommand(
                "UPDATE claimcore.installation_lineage SET loss_retired=true,"
                + "loss_retirement_id=@id,loss_retirement_intent_sequence=@sequence,"
                + "loss_retirement_intent_hash=@hash WHERE singleton",
                context.Primary
            )

        command.Parameters.AddWithValue("id", decision.Value.RetirementId) |> ignore

        command.Parameters.AddWithValue("sequence", decision.BeforeSequence + 1L)
        |> ignore

        let intent =
            (context.Witness.EvidenceStore
                .TryReadEvidence(decision.Value.RetirementId, Intent, CancellationToken.None)
                .GetAwaiter()
                .GetResult())
            |> Option.defaultWith (fun () -> failtest "Surviving W0 was absent.")

        command.Parameters.AddWithValue("hash", intent.Ticket.EntryHash) |> ignore
        Expect.equal (command.ExecuteNonQuery()) 1 "Synthetic primary projection restored."

let private witnessRollback (context: Context) (decision: Decision) =
    use owner = new NpgsqlConnection(witnessOwnerFor context.Writer)
    owner.Open()
    use transaction = owner.BeginTransaction()

    execute
        owner
        transaction
        "ALTER TABLE claimcore_witness.installation DISABLE TRIGGER guard_loss_installation"

    let delete sql =
        use command = new NpgsqlCommand(sql, owner, transaction)
        command.Parameters.AddWithValue("cutoff", decision.BeforeSequence) |> ignore
        command.ExecuteNonQuery() |> ignore

    delete "DELETE FROM claimcore_witness.journal_payloads WHERE sequence>@cutoff"
    delete "DELETE FROM claimcore_witness.journal WHERE sequence>@cutoff"
    execute owner transaction "DELETE FROM claimcore_witness.installation_loss_retirements"

    use rollback =
        new NpgsqlCommand(
            "UPDATE claimcore_witness.installation SET loss_retirement_pending=false,"
            + "loss_retired=false,loss_retirement_id=NULL,loss_retirement_intent_sequence=NULL,"
            + "loss_retirement_intent_hash=NULL,loss_retirement_sequence=NULL,"
            + "loss_retirement_hash=NULL,tip_sequence=@cutoff,tip_hash=@hash WHERE singleton",
            owner,
            transaction
        )

    rollback.Parameters.AddWithValue("cutoff", decision.BeforeSequence) |> ignore
    rollback.Parameters.AddWithValue("hash", decision.Value.PreviousHash) |> ignore
    Expect.equal (rollback.ExecuteNonQuery()) 1 "Only synthetic witness tip rolled back."

    execute
        owner
        transaction
        "ALTER TABLE claimcore_witness.installation ENABLE TRIGGER guard_loss_installation"

    transaction.Commit()

let private stalePair (context: Context) =
    let operationId, reference, artifact = acceptedArtifact context

    let decision =
        prepare context (knownSource operationId) InstallationLossOperationSet.Known

    match
        InstallationLossRetirementAdministration.record
            context.Primary
            (witnessOwnerFor context.Writer)
            context.Witness
            context.Suppression
            decision.Canonical
            decision.FirstSignature
            decision.SecondSignature
            decision.KnownSource
            None
            None
        |> await
    with
    | InstallationLossRetirementOutcome.Retired _ -> ()
    | _ -> failtest "Synthetic accepted operation was not terminally fenced."

    primaryRollback context decision reference artifact
    use clean = new NpgsqlConnection(context.OwnerConnectionString)
    clean.Open()
    DataAudit.run clean context.Witness CancellationToken.None |> await |> ignore
    witnessRollback context decision
    (context.Witness.AdmitReadOnly(CancellationToken.None).GetAwaiter().GetResult())

    Expect.isFalse
        ((context.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).LossRetired)
        "Only the synthetic witness state rolled back; its role/catalog remain admissible."

    auditRejected context
    closed context reference artifact

let tests =
    testList
        "installation loss stale pair"
        [
            testCase
                "[CC-BACKUP-001] one-cluster stale primary or witness cannot reopen accepted operation and artifact"
                (fun _ ->
                    withAuthorityRuntimeDatabase (fun owner app writer witness ->
                        InstallationLossRetirementFixture.run owner app writer witness stalePair))
        ]
