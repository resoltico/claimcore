module ClaimCore.IntegrationTests.WitnessedCapacityTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.AuthorityOperationFenceFixture

let private grantWork app witness principal =
    use source = RuntimeDataSource.create app
    let grants = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    for role in [ Role.CaseEditor; Role.RecoveryOperator; Role.RecoveryExporter ] do
        registry.SetGrant(
            principal,
            actorId grants principal,
            {
                Role = role
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

let private acceptedExecution (core: IActorClaimsCore) request =
    match core.Execute(request, CancellationToken.None) |> await with
    | SubmissionOutcome.Completed(_,
                                  _,
                                  DefiniteExecution.Accepted _,
                                  SettlementConfirmation.Confirmed) -> ()
    | _ -> failtest "Synthetic command must settle before capacity measurements."

let private populate (core: IActorClaimsCore) =
    let prefix = "WIT-CAP-" + Guid.NewGuid().ToString("N")

    for n in 1..55 do
        acceptedExecution core (openRequest (Guid.NewGuid()) (prefix + sprintf "-%03d" n))

    let reference = prefix + "-001"

    for version in 1L .. 60L do
        let command = if version % 2L = 1L then Command.Close else Command.Reopen

        acceptedExecution
            core
            {
                OperationId = Guid.NewGuid()
                CaseReference = reference
                ExpectedVersion = version
                Command = command
            }

    reference

let private verifyPages (core: IActorClaimsCore) reference =
    let first =
        core.List({ AfterCursor = None; Limit = 50 }, CancellationToken.None) |> await

    match first with
    | QueryOutcome.Succeeded value ->
        Expect.equal value.Items.Length 50 "First case window is bounded."

        match value.NextCursor with
        | None -> failtest "Case continuation must exist."
        | Some cursor ->
            match
                core.List(
                    {
                        AfterCursor = Some cursor
                        Limit = 50
                    },
                    CancellationToken.None
                )
                |> await
            with
            | QueryOutcome.Succeeded tail ->
                Expect.equal tail.Items.Length 5 "All remaining cases are reached once."
            | _ -> failtest "Expected a second case page."
    | _ -> failtest "Expected an authorized first case page."

    let history cursor =
        core.History(
            {
                CaseReference = reference
                AfterCursor = cursor
                Limit = 50
                Detail = HistoryDetail.Full
            },
            CancellationToken.None
        )
        |> await

    match history None with
    | QueryOutcome.Succeeded(Lookup.Found page) ->
        Expect.equal page.Entries.Length 50 "History is one bounded window."

        match history page.NextCursor with
        | QueryOutcome.Succeeded(Lookup.Found tail) ->
            Expect.equal tail.Entries.Length 11 "History continuation has no duplicates."
        | _ -> failtest "Expected a second witnessed history page."
    | _ -> failtest "Expected witnessed accepted history."

let private exportPreparation (core: IActorClaimsCore) reference =
    let request =
        {
            OperationId = Guid.NewGuid()
            CaseReference = reference
            ExpectedVersion = 61L
            Command = Command.Close
        }

    match core.Prepare(request, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Expected exact digest.")

        match
            core.Recovery.ExportEnvelope(request.OperationId, digest, CancellationToken.None)
            |> await
        with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) -> ()
        | _ -> failtest "Expected a witnessed export."
    | _ -> failtest "Expected retained preparation."

let private holdAuditAndResume
    owner
    (core: IActorClaimsCore)
    (auditResources: RuntimeResources)
    reference
    =
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 15.)
    let fenced = signal ()
    let hold = signal ()

    let audit =
        RuntimeFullAudit.runWith
            auditResources
            (fun () -> Task.CompletedTask)
            (fun () ->
                fenced.SetResult()
                hold.Task.WaitAsync(timeout.Token) :> Task)
            timeout.Token

    try
        Expect.isTrue (fenced.Task.Wait(2000)) "Audit holds its stable cutoff."

        let mutation =
            Task.Run<SubmissionOutcome>(fun () ->
                core.Execute(
                    openRequest (Guid.NewGuid()) (reference + "-NEXT"),
                    CancellationToken.None
                ))

        let read =
            Task.Run<QueryOutcome<Lookup<CurrentCase, string>>>(fun () ->
                core.Get(reference, CancellationToken.None))

        waitForDatabaseLock owner "transactionid"
        Expect.isFalse mutation.IsCompleted "Mutation waits behind audit authority."
        Expect.isFalse read.IsCompleted "Disclosure waits behind the stable audit."
        timeout.Cancel()

        try
            completed audit |> ignore
            failtest "Audit must report cancellation."
        with :? OperationCanceledException ->
            ()

        match completed mutation with
        | SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _) -> ()
        | _ -> failtest "Admitted mutation must keep its accepted result after audit cancellation."

        match completed read with
        | QueryOutcome.Succeeded(Lookup.Found _) -> ()
        | _ -> failtest "Queued read must resume without pool exhaustion."

        let final = RuntimeFullAudit.run auditResources CancellationToken.None |> completed
        Expect.equal final.Cases 56L "Post-contention audit includes the accepted operation."
    finally
        hold.TrySetResult() |> ignore
        timeout.Cancel()

let private competingAuditAndActorWork () =
    withAuthorityRuntimeDatabase (fun owner app writer witness ->
        let principal = human "witnessed-capacity-owner"
        provision owner witness principal |> applied
        grantWork app witness principal

        use runtime =
            Runtime.OpenPostgres(
                pooled app 2,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
            |> accepted

        let core = runtime.ForActor principal
        let reference = populate core
        verifyPages core reference
        exportPreparation core reference
        use auditResources = resources app witness

        let summary =
            RuntimeFullAudit.run auditResources CancellationToken.None |> completed

        Expect.equal summary.Cases 55L "Full audit crosses its case page boundary."

        Expect.equal
            summary.AcceptedOperations
            115L
            "Full replay crosses multiple history windows."

        holdAuditAndResume owner core auditResources reference)

let tests =
    testList
        "witnessed capacity and competing work"
        [
            testCase
                "[CC-AUDIT-001] paged witnessed volume export and actor work survive audit contention with small pools"
                competingAuditAndActorWork
        ]
