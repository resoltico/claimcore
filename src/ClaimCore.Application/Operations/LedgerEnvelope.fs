namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

[<NoEquality; NoComparison>]
type internal LedgerAppend<'projection> =
    {
        OperationId: OperationId
        ObservedAt: DateTimeOffset
        ActorId: ActorId
        RecordedBy: ActorRef option
        Command: CommandSkeleton
        Values: OperationValues
        Current: ReceiptCurrent
        Projection: RetainedOperation -> 'projection
    }

module internal LedgerEnvelope =
    let stamp (operation: RetainedOperation) =
        {
            SubjectStamp.Position =
                EffectPosition.create operation.Root.Header.Sequence 0 |> Option.get
            OperationId = operation.OperationId
            ActorId = operation.ActorId
            RecordedAt = operation.Root.Header.RecordedAt
        }

    let actorId (view: AccessView) =
        view.Actor
        |> Option.map _.ActorId
        |> Option.defaultWith (fun () -> invalidArg (nameof view) "Authorized actor is missing.")

    let private verdict =
        function
        | None -> Verdict.Proceed()
        | Some refusal -> Verdict.Refuse refusal

    let frontier expiredBefore observedAt operation =
        OperationLifetime.frontierRefusal expiredBefore observedAt operation |> verdict

    let lifetime expiredBefore observedAt operation =
        OperationLifetime.refusal expiredBefore observedAt operation |> verdict

    let accept
        (services: ServiceContext)
        actor
        (write: LedgerWriteContext)
        (plan: LedgerAppend<'Projection>)
        =
        try
            let businessDate =
                match plan.Command with
                | CommandSkeleton.People _ -> None
                | _ -> Some(services.Calendar.DateAt plan.ObservedAt)

            let header =
                {
                    OperationHeader.InstallationId = services.InstallationId
                    Sequence = Sequence.successor write.Head.Sequence
                    PreviousHash = write.Head.EntryHash
                    OperationId = plan.OperationId
                    ActorId = plan.ActorId
                    RecordedAt = plan.ObservedAt
                    BusinessDate = businessDate
                }

            RetainedOperation.create header plan.Command plan.Values write.NewPayloadNonce
            |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
            |> Result.bind (fun operation ->
                LedgerEntries.validateOperation services.InstallationId operation
                |> Result.bind (
                    LedgerEntries.receiptOperation
                        (ServiceContext.basis services plan.ObservedAt)
                        services.Principal
                        actor
                        plan.RecordedBy
                        plan.Current
                )
                |> Result.map (fun receipt ->
                    Verdict.Proceed(operation, plan.Projection operation, receipt)))
            |> Result.defaultWith Verdict.Fail
        with :? OverflowException ->
            Verdict.Fail CoreFault.InternalError
