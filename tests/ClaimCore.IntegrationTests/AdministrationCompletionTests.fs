module ClaimCore.IntegrationTests.AdministrationCompletionTests

open System
open System.IO
open Expecto
open ClaimCore.Postgres

let private beforeAdmission () =
    let result =
        AdministrationExecution.run (fun (_: AdministrationProgress<int>) ->
            AdministrationFailures.refuse AdministrationFailure.OwnerIdentityRejected)

    match result with
    | AdministrationOutcome.NotStarted AdministrationFailure.OwnerIdentityRejected -> ()
    | _ -> failtest "Owner refusal must precede work"

let private beforeCommit () =
    let result =
        AdministrationExecution.run (fun (progress: AdministrationProgress<int>) ->
            progress.BeginWork()
            raise (IOException("PRIVATE-provider-details")))

    match result with
    | AdministrationOutcome.NotCommitted AdministrationFailure.DatabaseUnavailable -> ()
    | _ -> failtest "No commit was attempted"

let private duringCommit () =
    let mutable commits = 0

    let result =
        AdministrationExecution.run (fun progress ->
            progress.BeginWork()

            progress.Commit(
                (fun () ->
                    commits <- commits + 1
                    raise (IOException("PRIVATE-commit-confirmation"))),
                42
            ))

    match result with
    | AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed -> ()
    | _ -> failtest "A commit exception cannot prove rollback"

    Expect.equal commits 1 "No internal commit replay"

let private cleanupFailure () =
    let result =
        AdministrationExecution.run (fun progress ->
            use _cleanup =
                { new IDisposable with
                    member _.Dispose() = raise (IOException("PRIVATE-disposal"))
                }

            progress.BeginWork()
            progress.Commit(ignore, 42))

    match result with
    | AdministrationOutcome.CompletedCleanupFailed 42 -> ()
    | _ -> failtest "Disposal cannot replace a confirmed result"

let private confirmedValue () =
    let result =
        AdministrationExecution.run (fun progress ->
            progress.BeginWork()
            progress.Commit(ignore, 42) |> ignore
            99)

    match result with
    | AdministrationOutcome.Completed 42 -> ()
    | _ -> failtest "The confirmed checkpoint, not later reporting, owns the result"

let private noSecondCommit () =
    let mutable commits = 0

    let result =
        AdministrationExecution.run (fun progress ->
            progress.BeginWork()
            let commit () = commits <- commits + 1
            progress.Commit(commit, 42) |> ignore
            progress.Commit(commit, 99))

    match result with
    | AdministrationOutcome.CompletedCleanupFailed 42 -> ()
    | _ -> failtest "Attempting a second commit must not erase the original completion"

    Expect.equal commits 1 "Exactly one commit was called"

let tests =
    testList
        "administration completion knowledge"
        [
            testCase "owner admission refusal cannot start maintenance" beforeAdmission
            testCase "precommit failure is distinct from unconfirmed commit" beforeCommit
            testCase "commit exception preserves uncertainty without retry" duringCommit
            testCase "postcommit disposal failure preserves the confirmed result" cleanupFailure
            testCase "the confirmed checkpoint owns the returned value" confirmedValue
            testCase "a second administrative commit cannot be issued" noSecondCommit
        ]
