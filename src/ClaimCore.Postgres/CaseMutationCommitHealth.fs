namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Only the Hosting composition root can install this scoped, typed guard. Owner
/// administration has no scope and remains able to repair stale backup health.
type internal ICaseMutationCommitHealth =
    abstract VerifyLocked: NpgsqlConnection * NpgsqlTransaction * CancellationToken -> Task

module internal CaseMutationCommitHealth =
    type private Scope(guard: ICaseMutationCommitHealth) =
        let mutable active = 1
        let mutable checking = 0
        let mutable entered = 0
        let mutable verified = 0

        member _.Verify(connection, transaction, ct) =
            task {
                if Volatile.Read(&active) = 0 then
                    invalidOp "Case mutation health scope has ended."

                if Interlocked.CompareExchange(&checking, 1, 0) <> 0 then
                    invalidOp "Case mutation health recheck is reentrant."

                try
                    if Volatile.Read(&active) = 0 then
                        invalidOp "Case mutation health scope has ended."

                    Interlocked.Increment(&entered) |> ignore
                    do! guard.VerifyLocked(connection, transaction, ct)
                    Interlocked.Increment(&verified) |> ignore
                finally
                    Volatile.Write(&checking, 0)
            }

        member _.Entered = Volatile.Read(&entered) > 0
        member _.Verified = Volatile.Read(&verified) > 0

        member _.Close() =
            Interlocked.Exchange(&active, 0) |> ignore

    let private current = AsyncLocal<Scope option>()

    let enter (guard: ICaseMutationCommitHealth) =
        if isNull (box guard) then
            invalidArg (nameof guard) "Commit health guard is required."

        let prior = current.Value
        let scope = Scope(guard)
        current.Value <- Some scope

        { new IDisposable with
            member _.Dispose() =
                scope.Close()
                current.Value <- prior
        }

    let verifyLocked connection transaction ct =
        match current.Value with
        | None -> Task.CompletedTask
        | Some scope -> scope.Verify(connection, transaction, ct) :> Task

    let requireVerified () =
        match current.Value with
        | Some scope when scope.Verified -> ()
        | Some scope when scope.Entered -> invalidOp "Case mutation commit-side health was refused."
        | _ -> invalidOp "Case mutation never reached commit-side health admission."
