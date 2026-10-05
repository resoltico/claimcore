namespace ClaimCore.Hosting

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal RuntimeUseGate =
    {
        RequireCaseMutation: CancellationToken -> Task<unit>
        RequireCaseRead: CancellationToken -> Task<unit>
        RequireAuthoritySetup: CancellationToken -> Task<unit>
        RequireAuthorityRead: CancellationToken -> Task<unit>
        CommitHealth: ICaseMutationCommitHealth
        CommitHealthRequired: bool
    }

/// Closes admission before draining; a timed-out disposer leaves final cleanup to the last lease.
type internal RuntimeAdmission
    (
        dataSource: IDisposable,
        drainTimeout: TimeSpan,
        requireCurrent: CancellationToken -> Task<unit>,
        acquireReadFence: CancellationToken -> Task<IDisposable>,
        useGate: RuntimeUseGate
    ) =
    let gate = obj ()

    let drained =
        TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable closing = false
    let mutable active = 0
    let mutable cleanupStarted = false

    let cleanup () =
        Task.Run(fun () ->
            let success =
                try
                    dataSource.Dispose()
                    true
                with _ ->
                    false

            drained.TrySetResult(success) |> ignore)
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

        lease

    member this.RunClassified
        (
            work: unit -> Task<'value>,
            isPreAdmissionRefusal: 'value -> bool,
            ?validateDisclosure: 'value -> Task,
            ?cancellationToken: CancellationToken,
            ?onCancelled: unit -> 'value
        ) =
        task {
            let mutable dispatched = false

            try
                let ct = defaultArg cancellationToken CancellationToken.None
                use _lease = this.Admit()
                do! requireCurrent ct
                do! useGate.RequireCaseMutation ct
                use _commitHealth = CaseMutationCommitHealth.enter useGate.CommitHealth
                dispatched <- true
                let! outcome = work ()

                if useGate.CommitHealthRequired && not (isPreAdmissionRefusal outcome) then
                    CaseMutationCommitHealth.requireVerified ()
                // A witnessed mutation can settle just before a handoff fences disclosure.
                // Recheck after settlement so no claimant-bearing outcome escapes this core boundary.
                use! _disclosureFence = acquireReadFence CancellationToken.None

                match validateDisclosure with
                | Some validate -> do! validate outcome
                | None -> ()

                return outcome
            with :? OperationCanceledException as error when
                (defaultArg cancellationToken CancellationToken.None).IsCancellationRequested
                && not dispatched ->
                match onCancelled with
                | Some outcome -> return outcome ()
                | None -> return raise error
        }

    member this.Run(work: unit -> Task<'value>, ?cancellationToken, ?onCancelled) =
        this.RunClassified(
            work,
            (fun _ -> false),
            ?cancellationToken = cancellationToken,
            ?onCancelled = onCancelled
        )

    member this.RunDisclosing
        (
            work: unit -> Task<'value>,
            validateDisclosure: 'value -> Task,
            ?cancellationToken,
            ?onCancelled
        ) =
        this.RunClassified(
            work,
            (fun _ -> false),
            validateDisclosure,
            ?cancellationToken = cancellationToken,
            ?onCancelled = onCancelled
        )

    member this.RunRead
        (
            work: unit -> Task<'value>,
            ?cancellationToken: CancellationToken,
            ?onCancelled: unit -> 'value
        ) =
        task {
            let mutable dispatched = false

            try
                let ct = defaultArg cancellationToken CancellationToken.None
                use _lease = this.Admit()
                do! requireCurrent ct
                do! useGate.RequireCaseRead ct
                use! _fence = acquireReadFence ct
                dispatched <- true
                return! work ()
            with :? OperationCanceledException as error when
                (defaultArg cancellationToken CancellationToken.None).IsCancellationRequested ->
                match onCancelled with
                | Some outcome -> return outcome ()
                | None -> return raise error
        }

    member this.RunAuthoritySetup
        (
            work: unit -> Task<'value>,
            ?cancellationToken: CancellationToken,
            ?onCancelled: unit -> 'value
        ) =
        task {
            let mutable dispatched = false

            try
                let ct = defaultArg cancellationToken CancellationToken.None
                use _lease = this.Admit()
                do! requireCurrent ct
                do! useGate.RequireAuthoritySetup ct
                dispatched <- true
                let! outcome = work ()
                use! _fence = acquireReadFence CancellationToken.None
                return outcome
            with :? OperationCanceledException as error when
                (defaultArg cancellationToken CancellationToken.None).IsCancellationRequested
                && not dispatched ->
                match onCancelled with
                | Some outcome -> return outcome ()
                | None -> return raise error
        }

    member this.RunAuthorityRead
        (
            work: unit -> Task<'value>,
            ?cancellationToken: CancellationToken,
            ?onCancelled: unit -> 'value
        ) =
        task {
            let mutable dispatched = false

            try
                let ct = defaultArg cancellationToken CancellationToken.None
                use _lease = this.Admit()
                do! requireCurrent ct
                do! useGate.RequireAuthorityRead ct
                use! _fence = acquireReadFence ct
                dispatched <- true
                return! work ()
            with :? OperationCanceledException as error when
                (defaultArg cancellationToken CancellationToken.None).IsCancellationRequested ->
                match onCancelled with
                | Some outcome -> return outcome ()
                | None -> return raise error
        }

    member _.CleanupCompletion = drained.Task

    member this.CloseAndDrain(stopBackgroundWork: unit -> unit) =
        let shouldCleanup =
            lock gate (fun () ->
                closing <- true

                if active = 0 && not cleanupStarted then
                    cleanupStarted <- true
                    true
                else
                    false)

        try
            try
                stopBackgroundWork ()
            finally
                if shouldCleanup then
                    cleanup ()

            if this.CleanupCompletion.Wait(drainTimeout) then
                if not (this.CleanupCompletion.GetAwaiter().GetResult()) then
                    invalidOp "ClaimCore runtime cleanup failed."
        with _ ->
            raise (InvalidOperationException("ClaimCore runtime cleanup failed."))

    interface IDisposable with
        member this.Dispose() = this.CloseAndDrain(fun () -> ())
