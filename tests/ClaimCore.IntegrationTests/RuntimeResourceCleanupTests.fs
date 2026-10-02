module ClaimCore.IntegrationTests.RuntimeResourceCleanupTests

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Hosting

let private attemptsEveryResource () =
    let attempted = ResizeArray<int>()

    let resource index throws =
        { new IDisposable with
            member _.Dispose() =
                attempted.Add(index)

                if throws then
                    invalidOp "Synthetic private provider canary"
        }

    let error =
        try
            RuntimeResourceCleanup.disposeAll
                [ resource 1 true; resource 2 false; resource 3 true; resource 4 false ]

            None
        with :? InvalidOperationException as failure ->
            Some failure

    Expect.equal (Seq.toList attempted) [ 1; 2; 3; 4 ] "Every owned resource was attempted in order"

    Expect.equal
        (error |> Option.map _.Message)
        (Some "ClaimCore runtime resource cleanup failed.")
        "Failure is bounded without provider details"

let private closingPrecedesCleanupWait () =
    let entered =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let finish =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let empty () =
        { new IDisposable with
            member _.Dispose() = ()
        }

    let source =
        { new IDisposable with
            member _.Dispose() =
                entered.TrySetResult() |> ignore
                finish.Task.GetAwaiter().GetResult()
        }

    use admission =
        new RuntimeAdmission(
            source,
            TimeSpan.FromMilliseconds 50.,
            (fun () -> ()),
            empty,
            {
                RequireCaseMutation = (fun () -> ())
                RequireCaseRead = (fun () -> ())
                RequireAuthoritySetup = (fun () -> ())
                RequireAuthorityRead = (fun () -> ())
                CommitHealth =
                    { new ClaimCore.Postgres.ICaseMutationCommitHealth with
                        member _.VerifyLocked(_, _) = ()
                    }
                CommitHealthRequired = false
            }
        )

    try
        (admission :> IDisposable).Dispose()
        Expect.isTrue (entered.Task.Wait(2000)) "Cleanup can remain active after bounded disposal"

        Expect.throwsT<ObjectDisposedException>
            (fun () -> admission.Admit() |> ignore)
            "No new work enters while cleanup waits"
    finally
        finish.TrySetResult() |> ignore

let tests =
    testList
        "runtime cleanup ownership"
        [
            testCase
                "[CC-RUN-001] runtime cleanup attempts every resource despite disposal faults"
                attemptsEveryResource
            testCase
                "[CC-RUN-001] runtime closes admission before cleanup completion"
                closingPrecedesCleanupWait
        ]
