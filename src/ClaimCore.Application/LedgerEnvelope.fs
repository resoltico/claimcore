namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

type internal LedgerAppend<'projection> =
    {
        OperationId: OperationId
        ObservedAt: DateTimeOffset
        ActorId: ActorId
        Subject: EntryTarget
        Snapshot: byte array
        Current: StoredCase option
        Projection: 'projection
    }

type internal LedgerWriteContext =
    {
        Head: LedgerPosition
        Nonce: byte array
        Fenced: bool
    }

/// Common ledger sequencing and receipt construction, independent of a use case.
module internal LedgerEnvelope =
    let actorId (view: AuthorityView) =
        view.Actor
        |> Option.map _.ActorId
        |> Option.defaultWith (fun () -> invalidArg (nameof view) "Authorized actor is missing.")

    let guard fenced =
        if fenced then
            Verdict.Refuse Refusal.OperationResolved
        else
            Verdict.Proceed()

    let accept
        (services: ServiceContext)
        actor
        (write: LedgerWriteContext)
        (plan: LedgerAppend<'Projection>)
        bytes
        =
        let installationId = services.InstallationId
        let principal = services.Principal

        try
            let entry =
                LedgerEntries.create
                    {
                        InstallationId = installationId
                        Head = write.Head
                        Instant = plan.ObservedAt
                        Nonce = write.Nonce
                        ActorId = plan.ActorId
                        OperationId = plan.OperationId
                        Subject = plan.Subject
                        Request = bytes
                        Snapshot = plan.Snapshot
                    }

            LedgerEntries.validate installationId (LedgerEntries.stored entry)
            |> Result.bind (LedgerEntries.receipt principal actor plan.Current)
            |> Result.map (fun receipt -> Verdict.Proceed(entry, plan.Projection, receipt))
            |> Result.defaultWith Verdict.Fail
        with :? OverflowException ->
            Verdict.Fail CoreFault.InternalError
