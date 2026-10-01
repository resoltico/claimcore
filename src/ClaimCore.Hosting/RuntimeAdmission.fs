namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal RuntimeUseGate =
    {
        RequireCaseMutation: unit -> unit
        RequireCaseRead: unit -> unit
        RequireAuthoritySetup: unit -> unit
        RequireAuthorityRead: unit -> unit
        CommitHealth: ICaseMutationCommitHealth
        CommitHealthRequired: bool
    }

/// Closes admission before draining; a timed-out disposer leaves final cleanup to the last lease.
type internal RuntimeAdmission
    (
        dataSource: IDisposable,
        drainTimeout: TimeSpan,
        requireCurrent: unit -> unit,
        acquireReadFence: unit -> IDisposable,
        useGate: RuntimeUseGate
    ) =
    let gate = obj ()

    let drained =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable closing = false
    let mutable active = 0
    let mutable cleanupStarted = false

    let cleanup () =
        Task.Run(fun () ->
            try
                dataSource.Dispose()
                drained.TrySetResult() |> ignore
            with error ->
                drained.TrySetException(error) |> ignore)
        |> ignore

    let release () =
        let shouldCleanup =
            lock gate (fun () ->
                active <- active - 1

                if closing && active = 0 && not cleanupStarted then
                    cleanupStarted <- true
                    true
                else
                    false)

        if shouldCleanup then
            cleanup ()

    do
        if isNull (box dataSource) then
            nullArg (nameof dataSource)

        if drainTimeout <= TimeSpan.Zero then
            invalidArg (nameof drainTimeout) "The runtime drain timeout must be positive."

        if
            isNull (box requireCurrent)
            || isNull (box acquireReadFence)
            || isNull (box useGate)
        then
            invalidArg (nameof requireCurrent) "Runtime safety checks are required."

    member _.Admit() =
        let lease =
            lock gate (fun () ->
                if closing then
                    raise (ObjectDisposedException("ClaimCore Runtime"))

                active <- active + 1
                let mutable released = 0

                { new IDisposable with
                    member _.Dispose() =
                        if Interlocked.Exchange(&released, 1) = 0 then
                            release ()
                })

        try
            requireCurrent ()
            lease
        with _ ->
            lease.Dispose()
            reraise ()

    member this.RunClassified
        (
            work: unit -> Task<'value>,
            isPreAdmissionRefusal: 'value -> bool,
            ?validateDisclosure: 'value -> Task
        ) =
        task {
            use _lease = this.Admit()
            useGate.RequireCaseMutation()
            use _commitHealth = CaseMutationCommitHealth.enter useGate.CommitHealth
            let! outcome = work ()

            if useGate.CommitHealthRequired && not (isPreAdmissionRefusal outcome) then
                CaseMutationCommitHealth.requireVerified ()
            // A witnessed mutation can settle just before a handoff fences disclosure.
            // Recheck after settlement so no claimant-bearing outcome escapes this core boundary.
            use _disclosureFence = acquireReadFence ()

            match validateDisclosure with
            | Some validate -> do! validate outcome
            | None -> ()

            return outcome
        }

    member this.Run(work: unit -> Task<'value>) =
        this.RunClassified(work, fun _ -> false)

    member this.RunDisclosing(work: unit -> Task<'value>, validateDisclosure: 'value -> Task) =
        this.RunClassified(work, (fun _ -> false), validateDisclosure)

    member this.RunRead(work: unit -> Task<'value>) =
        task {
            use _lease = this.Admit()
            useGate.RequireCaseRead()
            use _fence = acquireReadFence ()
            return! work ()
        }

    member this.RunAuthoritySetup(work: unit -> Task<'value>) =
        task {
            use _lease = this.Admit()
            useGate.RequireAuthoritySetup()
            let! outcome = work ()
            use _fence = acquireReadFence ()
            return outcome
        }

    member this.RunAuthorityRead(work: unit -> Task<'value>) =
        task {
            use _lease = this.Admit()
            useGate.RequireAuthorityRead()
            use _fence = acquireReadFence ()
            return! work ()
        }

    interface IDisposable with
        member _.Dispose() =
            let shouldCleanup =
                lock gate (fun () ->
                    closing <- true

                    if active = 0 && not cleanupStarted then
                        cleanupStarted <- true
                        true
                    else
                        false)

            if shouldCleanup then
                cleanup ()

            try
                if drained.Task.Wait(drainTimeout) then
                    drained.Task.GetAwaiter().GetResult()
            with _ ->
                raise (InvalidOperationException("ClaimCore runtime cleanup failed."))
