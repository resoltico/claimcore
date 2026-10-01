module ClaimCore.IntegrationTests.AuthorityOperationFenceCloseTests

open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let tests =
    testCase
        "[CC-AUDIT-001] authority lease cleanup preserves an observed result and retires its connector"
        (fun () ->
            withAuthorityRuntimeDatabase (fun owner app _ _ ->
                use source = RuntimeDataSource.create app
                use primary = source.OpenConnection()
                let pid = primary.ProcessID

                let observed =
                    task {
                        use! _lease =
                            AuthorityOperationFence.acquireShared
                                (Some source)
                                primary
                                CancellationToken.None

                        use controller = new NpgsqlConnection(owner)
                        controller.Open()

                        use stop =
                            new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", controller)

                        stop.Parameters.AddWithValue("pid", pid) |> ignore

                        Expect.equal
                            (stop.ExecuteScalar() :?> bool)
                            true
                            "Exact synthetic backend is terminated"

                        return 42
                    }
                    |> await

                Expect.equal observed 42 "Cleanup cannot rewrite already observed knowledge"

                Expect.equal
                    primary.State
                    System.Data.ConnectionState.Closed
                    "Ambiguous connector is closed"

                use next = source.OpenConnection()

                Expect.notEqual
                    next.ProcessID
                    pid
                    "Terminated connector cannot return to the pool"

                use exclusive =
                    AuthorityOperationFence.acquireExclusive
                        (Some source)
                        next
                        CancellationToken.None
                    |> await

                ()))
