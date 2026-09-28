module ClaimCore.IntegrationTests.ActorBoundCoreTests

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.FreshBaselineSupport
open ClaimCore.IntegrationTests.ActorBoundCoreTestSupport

let private openRuntime app witnessWriter =
    Runtime.OpenPostgres(
        app,
        witnessWriter,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        CancellationToken.None
    )
    |> await
    |> accepted

let private grantEditor source witness ownerPrincipal =
    let store = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    let grant =
        {
            Role = Role.CaseEditor
            Scope = GrantScope.Installation
        }

    registry.SetGrant(ownerPrincipal, actorId store ownerPrincipal, grant, true)
    |> await
    |> applied

    registry, grant

let private requireUnavailable =
    function
    | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
    | _ -> failtest "The actor-bound endpoint must return one non-disclosing refusal."

let private submissionKind =
    function
    | SubmissionOutcome.ObservedAccepted _ -> "OBSERVED_ACCEPTED"
    | SubmissionOutcome.Completed _ -> "COMPLETED_OTHER"
    | SubmissionOutcome.RejectedBeforeAttempt _ -> "REJECTED_BEFORE_ATTEMPT"
    | SubmissionOutcome.FailedBeforeAttempt(_, fault) -> "FAILED_BEFORE_ATTEMPT:" + string fault
    | SubmissionOutcome.PreparationStateUnknown _ -> "PREPARATION_UNKNOWN"
    | SubmissionOutcome.CancelledBeforeAdmission _ -> "CANCELLED_BEFORE_ADMISSION"
    | SubmissionOutcome.CancelledBeforeAttempt _ -> "CANCELLED_BEFORE_ATTEMPT"
    | SubmissionOutcome.AttemptAdmissionUnknown _ -> "ATTEMPT_ADMISSION_UNKNOWN"
    | SubmissionOutcome.AttemptUnresolved _ -> "ATTEMPT_UNRESOLVED"

let private stageCounts owner =
    let count table =
        scalar owner ("SELECT count(*) FROM claimcore." + table) :?> int64

    count "request_preparations", count "request_submission_attempts", count "case_changes"

let private storedCaseId owner reference =
    use connection = new Npgsql.NpgsqlConnection(owner)
    connection.Open()

    use command =
        new Npgsql.NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    command.ExecuteScalar() :?> Guid

let private defaultDeny =
    testCase "[CC-AUTH-001] unknown actor and ungranted owner cannot reach case work" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let initial = human "initial-owner"
            provision owner witness initial |> applied
            use runtime = openRuntime app writer
            let unknown = runtime.ForActor(human "unregistered")
            unknown.Definition CancellationToken.None |> await |> requireUnavailable
            unknown.Get("CASE-X", CancellationToken.None) |> await |> requireUnavailable

            let request = openRequest (Guid.NewGuid()) ("DENY-" + Guid.NewGuid().ToString("N"))

            match unknown.Prepare(request, CancellationToken.None) |> await with
            | PrepareOutcome.PrepareRejected(_, Rejection.ResourceUnavailable) -> ()
            | _ -> failtest "Unknown subject cannot retain a preparation."

            match
                (runtime.ForActor initial).Prepare(request, CancellationToken.None) |> await
            with
            | PrepareOutcome.PrepareRejected(_, Rejection.ResourceUnavailable) -> ()
            | _ -> failtest "Owner role alone does not grant case editing."))

let private acceptedFlow =
    testCase
        "[CC-AUTH-001] actor-bound execute, read and exact replay enforce current grant"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let first = human "case-editor"
                provision owner witness first |> applied
                use source = RuntimeDataSource.create app
                let registry, _ = grantEditor source witness first
                use runtime = openRuntime app writer
                let core = runtime.ForActor first
                let input = openRequest (Guid.NewGuid()) ("ACTOR-" + Guid.NewGuid().ToString("N"))

                let outcome = core.Execute(input, CancellationToken.None) |> await

                match outcome with
                | SubmissionOutcome.Completed(_,
                                              _,
                                              DefiniteExecution.Accepted _,
                                              SettlementConfirmation.Confirmed) -> ()
                | _ ->
                    let preparations, attempts, accepted = stageCounts owner

                    failtestf
                        "Actor-bound OPEN was not definite: %s; stage counts %d/%d/%d."
                        (submissionKind outcome)
                        preparations
                        attempts
                        accepted

                match core.Get(input.CaseReference, CancellationToken.None) |> await with
                | QueryOutcome.Succeeded(Lookup.Found _) -> ()
                | _ -> failtest "Editor can read its accepted case."

                match
                    core.ObserveOperation(input.OperationId, CancellationToken.None) |> await
                with
                | QueryOutcome.Succeeded(Lookup.Found _) -> ()
                | _ -> failtest "Editor can observe its accepted operation."

                let other = human "other-editor"
                registry.RegisterActor(first, other) |> await |> applied

                match
                    (runtime.ForActor other).Execute(input, CancellationToken.None) |> await
                with
                | SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable) ->
                    ()
                | _ -> failtest "Exact replay by an ungranted actor discloses no receipt."))

let private revokeBetweenPrepareAndSubmit =
    testCase "[CC-AUTH-001] revoking case edit after prepare fences submit" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let first = human "revoked-editor"
            provision owner witness first |> applied
            use source = RuntimeDataSource.create app
            let registry, grant = grantEditor source witness first
            use runtime = openRuntime app writer
            let core = runtime.ForActor first
            let input = openRequest (Guid.NewGuid()) ("PREP-" + Guid.NewGuid().ToString("N"))

            match core.Prepare(input, CancellationToken.None) |> await with
            | PrepareOutcome.Prepared _ -> ()
            | _ -> failtest "Initial authorized preparation should be retained."

            let id = actorId (new ActorGrantStore(source)) first
            registry.SetGrant(first, id, grant, false) |> await |> applied

            match core.Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable) -> ()
            | _ -> failtest "Revoked editor cannot submit a retained preparation."

            let count =
                use connection = new Npgsql.NpgsqlConnection(owner)
                connection.Open()

                use command =
                    new Npgsql.NpgsqlCommand(
                        "SELECT count(*) FROM claimcore.case_changes WHERE operation_id=@operation",
                        connection
                    )

                command.Parameters.AddWithValue("operation", input.OperationId) |> ignore
                command.ExecuteScalar() :?> int64

            Expect.equal count 0L "Revocation left no accepted case event."))

let private noExistenceOracle =
    testCase "[CC-AUTH-001] inaccessible and absent resources share one refusal" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let first = human "oracle-owner"
            let other = human "oracle-other"
            provision owner witness first |> applied
            use source = RuntimeDataSource.create app
            let registry, _ = grantEditor source witness first
            registry.RegisterActor(first, other) |> await |> applied
            use runtime = openRuntime app writer
            let input = openRequest (Guid.NewGuid()) ("ORACLE-" + Guid.NewGuid().ToString("N"))

            match (runtime.ForActor first).Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.Completed(_,
                                          _,
                                          DefiniteExecution.Accepted _,
                                          SettlementConfirmation.Confirmed) -> ()
            | _ -> failtest "Synthetic accepted case is required."

            let denied = runtime.ForActor other

            denied.Get(input.CaseReference, CancellationToken.None)
            |> await
            |> requireUnavailable

            denied.Get("NO-SUCH-CASE", CancellationToken.None)
            |> await
            |> requireUnavailable

            denied.ObserveOperation(input.OperationId, CancellationToken.None)
            |> await
            |> requireUnavailable

            denied.ObserveOperation(Guid.NewGuid(), CancellationToken.None)
            |> await
            |> requireUnavailable

            requireComparableDenialTiming denied input.CaseReference input.OperationId

            let history reference =
                {
                    CaseReference = reference
                    AfterCursor = None
                    Limit = 10
                    Detail = HistoryDetail.Full
                }

            denied.History(history input.CaseReference, CancellationToken.None)
            |> await
            |> requireUnavailable

            denied.History(history "NO-SUCH-CASE", CancellationToken.None)
            |> await
            |> requireUnavailable

            let id = actorId (new ActorGrantStore(source)) other
            registry.SetEnabled(first, id, false) |> await |> applied
            denied.Definition CancellationToken.None |> await |> requireUnavailable))

let private listFiltersBeforeWindow =
    testCase "[CC-AUTH-001] case list filters grants before storage page window" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let first = human "list-owner"
            let reader = human "scoped-reader"
            provision owner witness first |> applied
            use source = RuntimeDataSource.create app
            let registry, _ = grantEditor source witness first
            registry.RegisterActor(first, reader) |> await |> applied
            use runtime = openRuntime app writer
            let finalReference = seedLateCase (runtime.ForActor first)

            let grant =
                {
                    Role = Role.CaseReader
                    Scope = GrantScope.Case(storedCaseId owner finalReference)
                }

            let target = actorId (new ActorGrantStore(source)) reader
            registry.SetGrant(first, target, grant, true) |> await |> applied
            let request = { AfterCursor = None; Limit = 10 }

            match (runtime.ForActor reader).List(request, CancellationToken.None) |> await with
            | QueryOutcome.Succeeded page ->
                Expect.equal
                    (page.Items |> List.map _.CaseReference)
                    [ finalReference ]
                    "Only the late granted case survives pre-window filtering."

                Expect.isNone page.NextCursor "Hidden cases cannot affect cursor."
            | _ -> failtest "Scoped actor list should contain one visible case."))

let tests =
    testList
        "actor-bound core"
        [
            defaultDeny
            acceptedFlow
            revokeBetweenPrepareAndSubmit
            noExistenceOracle
            listFiltersBeforeWindow
        ]
