namespace ClaimCore.Application

open System
open ClaimCore.Contract
open ClaimCore.Domain

type internal LedgerAppend<'projection> =
    {
        ObservedAt: DateTimeOffset
        ActorId: Guid
        Subject: StoredSubject
        Snapshot: byte array
        Current: StoredCase option
        Projection: 'projection
    }

[<NoEquality; NoComparison>]
type internal LedgerEnvelopeSubject<'read, 'proposal, 'projection> =
    {
        Encode: unit -> byte array
        Canonical: unit -> byte array
        Admit: byte array -> 'read -> Decision<unit>
        Propose: 'read -> Decision<'proposal>
        Project: 'read -> 'proposal -> LedgerAppend<'projection>
        Receipt: 'read -> StoredCase option -> ValidatedEntry -> Result<OperationReceipt, CoreFault>
    }

/// One sequencing rule for both ledgers; subject rules receive the immutable read view.
module internal LedgerEnvelope =
    let actorId (view: AuthorityView) =
        view.Actor
        |> Option.map _.ActorId
        |> Option.defaultWith (fun () -> invalidArg (nameof view) "Authorized actor is missing.")

    let review (subject: LedgerEnvelopeSubject<_, _, _>) read =
        subject.Admit (subject.Canonical()) read
        |> Decision.bind (fun () -> subject.Propose read)

    let execute
        installationId
        operationId
        (subject: LedgerEnvelopeSubject<_, _, _>)
        (head: LedgerHead)
        fenced
        nonce
        read
        =
        subject.Admit (subject.Canonical()) read
        |> Decision.bind (fun () ->
            if fenced then
                Decision.Refuse Refusal.OperationResolved
            elif head.Sequence = Revision.Maximum then
                Decision.Refuse Refusal.LedgerExhausted
            else
                subject.Propose read
                |> Decision.bind (fun proposal ->
                    let plan = subject.Project read proposal

                    let entry =
                        LedgerEntries.create
                            installationId
                            head
                            plan.ObservedAt
                            nonce
                            plan.ActorId
                            operationId
                            plan.Subject
                            (subject.Encode())
                            plan.Snapshot

                    LedgerEntries.validate installationId (LedgerEntries.stored entry)
                    |> Result.bind (subject.Receipt read plan.Current)
                    |> Result.map (fun receipt ->
                        Decision.Proceed(entry, plan.Projection, receipt))
                    |> Result.defaultWith Decision.Fail))
