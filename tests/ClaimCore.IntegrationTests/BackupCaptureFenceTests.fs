module ClaimCore.IntegrationTests.BackupCaptureFenceTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let private admittedRuntime app writer =
    Runtime.OpenPostgres(
        app,
        writer,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        CancellationToken.None
    )
    |> await
    |> accepted

let private editorGrant =
    {
        Role = Role.CaseEditor
        Scope = GrantScope.Installation
    }

let private capture owner source witness commitments =
    DatabaseBackupCaptureBarrier.Acquire(
        owner,
        source,
        witness,
        commitments,
        CancellationToken.None
    )
    |> await

let private caseWork
    (capture: unit -> DatabaseBackupCaptureBarrier)
    (actor: IActorClaimsCore)
    (witness: WitnessProtocol)
    =
    let request =
        openRequest (Guid.NewGuid()) ("BACKUP-" + Guid.NewGuid().ToString("N"))

    let first = capture ()
    let cutoff = first.Cutoff.WitnessSequence
    let detached = first.Cutoff
    detached.WitnessHash[0] <- detached.WitnessHash[0] ^^^ 1uy

    let submitted =
        Task.Run(fun () -> actor.Execute(request, CancellationToken.None) |> await)

    try
        Task.Delay(150).GetAwaiter().GetResult()
        Expect.isFalse submitted.IsCompleted "No accepted case commits behind capture fence."
        Expect.equal (witness.Snapshot().TipSequence) cutoff "Witness cutoff is fixed."
        let summary = first.Verify(CancellationToken.None) |> await
        Expect.equal summary.PendingIntents 0L "Held capture audit is complete."
    finally
        (first :> IDisposable).Dispose()

    match submitted.GetAwaiter().GetResult() with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Fenced operation did not settle after lease release."

let private grantChange
    (capture: unit -> DatabaseBackupCaptureBarrier)
    (registry: ActorGrantRegistry)
    (principal: PrincipalKey)
    (identity: Guid)
    (witness: WitnessProtocol)
    =
    let second = capture ()
    let cutoff = second.Cutoff.WitnessSequence

    let changed =
        Task.Run(fun () -> registry.SetGrant(principal, identity, editorGrant, false) |> await)

    try
        Task.Delay(150).GetAwaiter().GetResult()
        Expect.isFalse changed.IsCompleted "Owner grant mutation waits behind capture fence."
        Expect.equal (witness.Snapshot().TipSequence) cutoff "Owner authority cutoff is fixed."
        second.Verify(CancellationToken.None) |> await |> ignore
    finally
        (second :> IDisposable).Dispose()

    changed.GetAwaiter().GetResult() |> applied

let private capturedAuthority =
    testCase
        "[CC-BACKUP-001] witness read fence holds accepted and owner authority stable during capture"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let principal = human "backup-fence-owner-editor"
                provision owner witness principal |> applied
                use source = RuntimeDataSource.create app
                let registry = new ActorGrantRegistry(source, witness)
                let identity = actorId (new ActorGrantStore(source)) principal
                registry.SetGrant(principal, identity, editorGrant, true) |> await |> applied
                use runtime = admittedRuntime app writer
                use ownerConnection = new NpgsqlConnection(owner)
                ownerConnection.Open()
                let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

                let lease () =
                    capture ownerConnection source witness commitments

                caseWork lease (runtime.ForActor principal) witness
                grantChange lease registry principal identity witness
                use audit = RuntimeDatabase.openConnection source

                let summary =
                    DataAudit.runWithSuppression
                        audit
                        witness
                        (Some commitments)
                        CancellationToken.None
                    |> await

                Expect.equal summary.PendingIntents 0L "Released fence leaves no unknown intent."))

let tests = testList "owner backup capture fence" [ capturedAuthority ]
