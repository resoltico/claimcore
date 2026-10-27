namespace ClaimCore.Application

open System
open ClaimCore.Domain

type internal LedgerAppend<'projection> =
    {
        ObservedAt: DateTimeOffset
        ActorId: Guid
        Subject: EntryTarget
        Snapshot: byte array
        Current: StoredCase option
        Projection: 'projection
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
        installationId
        principal
        actor
        operationId
        head
        nonce
        (plan: LedgerAppend<'Projection>)
        bytes
        =
        try
            let entry =
                LedgerEntries.create
                    installationId
                    head
                    plan.ObservedAt
                    nonce
                    plan.ActorId
                    operationId
                    plan.Subject
                    bytes
                    plan.Snapshot

            LedgerEntries.validate installationId (LedgerEntries.stored entry)
            |> Result.bind (LedgerEntries.receipt principal actor plan.Current)
            |> Result.map (fun receipt -> Verdict.Proceed(entry, plan.Projection, receipt))
            |> Result.defaultWith Verdict.Fail
        with :? OverflowException ->
            Verdict.Fail CoreFault.InternalError
