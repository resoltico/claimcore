module internal ClaimCore.IntegrationTests.MutationDisclosureFixture

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let cancellation = CancellationToken.None

let grant source witness principal role active =
    let registry = new ActorGrantRegistry(source, witness)

    registry.SetGrant(
        principal,
        actorId (source) principal,
        {
            Role = role
            Scope = GrantScope.Installation
        },
        active
    )
    |> await
    |> applied

let fencedAdmission app (witness: WitnessProtocol) beforeFence =
    let fence () =
        beforeFence ()
        let connection = new NpgsqlConnection(app)
        connection.Open()
        let transaction = connection.BeginTransaction()

        use command =
            new NpgsqlCommand(
                "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR SHARE",
                connection,
                transaction
            )

        command.ExecuteScalar() |> ignore

        let witnessFence =
            (witness
                .AcquireReadFence(
                    (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .WriterGeneration,
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult())

        { new IDisposable with
            member _.Dispose() =
                witnessFence.Dispose()
                transaction.Dispose()
                connection.Dispose()
        }

    new RuntimeAdmission(
        { new IDisposable with
            member _.Dispose() = ()
        },
        TimeSpan.FromSeconds 10.,
        (fun ct -> witness.AdmitReadOnly(ct)),
        (fun _ -> task { return (fence) () }),
        {
            RequireCaseMutation = (fun _ -> Task.FromResult(()))
            RequireCaseRead = (fun _ -> Task.FromResult(()))
            RequireAuthoritySetup = (fun _ -> Task.FromResult(()))
            RequireAuthorityRead = (fun _ -> Task.FromResult(()))
            RequireAuditTrust = (fun () -> ())
            AuthorityHealth =
                { new IMutationCommitHealth with
                    member _.VerifyLocked(_, _, _) = Task.CompletedTask
                }
            CommitHealth =
                { new IMutationCommitHealth with
                    member _.VerifyLocked(_, _, _) = Task.CompletedTask
                }
            CommitHealthRequired = false
        }
    )

let prepared actor request =
    match (actor: IActorClaimsCore).Prepare(request, cancellation) |> await with
    | PrepareOutcome.Prepared(details, _) -> details.Summary.RequestSha256.Value
    | _ -> failtest "Synthetic preparation must be durable."

let exported actor operationId digest =
    match
        (actor: IActorClaimsCore).Recovery.ExportEnvelope(operationId, digest, cancellation)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) -> artifact
    | _ -> failtest "Synthetic export must be witnessed."

let retainedCount owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use query =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.request_preparations WHERE operation_id=@id",
            connection
        )

    Sql.uuid query "id" operationId
    query.ExecuteScalar() :?> int64

let acceptedCount owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use query =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_changes WHERE operation_id=@id",
            connection
        )

    Sql.uuid query "id" operationId
    query.ExecuteScalar() :?> int64

let requireWithheld work =
    Expect.throwsT<InvalidOperationException>
        work
        "Completed work cannot disclose after authorization is lost."

let erase (actor: IActorClaimsCore) reference =
    let proposal =
        change
            (Guid.NewGuid())
            reference
            (review actor reference)
            (LifecycleMutation.RequestErasure "Synthetic privacy fence")

    match actor.Lifecycle.Apply(proposal, cancellation) |> await with
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic erasure fence must be witnessed."
