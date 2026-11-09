namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

[<NoComparison>]
type internal LedgerAppend<'projection> =
    {
        OperationId: OperationId
        ObservedAt: DateTimeOffset
        ActorId: ActorId
        Command: CommandSkeleton
        Values: OperationValues
        Current: ReceiptCurrent
        Projection: 'projection
    }

type internal LedgerWriteContext =
    {
        Head: LedgerPosition
        Nonce: byte array
    }

module internal LedgerEnvelope =
    let actorId (view: AuthorityView) =
        view.Actor
        |> Option.map _.ActorId
        |> Option.defaultWith (fun () -> invalidArg (nameof view) "Authorized actor is missing.")

    let lifetime observedAt operation =
        if OperationLifetime.admits observedAt operation then
            Verdict.Proceed()
        else
            Verdict.Refuse(
                Refusal.OperationIdOutOfWindow(OperationId.issuedAt operation, observedAt)
            )

    let accept
        (services: ServiceContext)
        actor
        (write: LedgerWriteContext)
        (plan: LedgerAppend<'Projection>)
        =
        try
            let businessDate =
                match plan.Command with
                | CommandSkeleton.Authority _ -> None
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

            RetainedOperation.create header plan.Command plan.Values (fun () -> write.Nonce)
            |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
            |> Result.bind (fun operation ->
                LedgerEntries.validateOperation services.InstallationId operation
                |> Result.bind (
                    LedgerEntries.receiptOperation
                        (ServiceContext.basis services plan.ObservedAt)
                        services.Principal
                        actor
                        (actor.Actor |> Option.map Actor.currentReference)
                        plan.Current
                )
                |> Result.map (fun receipt -> Verdict.Proceed(operation, plan.Projection, receipt)))
            |> Result.defaultWith Verdict.Fail
        with :? OverflowException ->
            Verdict.Fail CoreFault.InternalError
