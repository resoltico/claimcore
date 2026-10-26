namespace ClaimCore.Application

open System
open ClaimCore.Contract
open ClaimCore.Domain

[<RequireQualifiedAccess; NoComparison>]
type internal LedgerReviewDecision<'proposal> =
    | Proposed of 'proposal
    | Replay of OperationReceipt
    | Refuse of Refusal
    | Fail of CoreFault

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
type internal LedgerEnvelopeScope<'read, 'proposal, 'projection> =
    {
        Encode: unit -> byte array
        Canonical: unit -> byte array
        Admit: byte array -> 'read -> IdentityDecision
        Propose: 'read -> LedgerReviewDecision<'proposal>
        Project: 'read -> 'proposal -> LedgerAppend<'projection>
        Receipt: 'read -> StoredCase option -> ValidatedEntry -> Result<OperationReceipt, CoreFault>
    }

/// One sequencing rule for both ledgers; subject rules receive the immutable read view.
module internal LedgerEnvelope =
    let actorId (view: AuthorityView) =
        view.Actor
        |> Option.map _.ActorId
        |> Option.defaultWith (fun () -> invalidArg (nameof view) "Authorized actor is missing.")

    let review (subject: LedgerEnvelopeScope<_, _, _>) read =
        match subject.Admit (subject.Canonical()) read with
        | IdentityDecision.Fresh -> subject.Propose read
        | IdentityDecision.Refuse refusal -> LedgerReviewDecision.Refuse refusal
        | IdentityDecision.Fail fault -> LedgerReviewDecision.Fail fault
        | IdentityDecision.Replay entry ->
            subject.Receipt read None entry
            |> Result.map LedgerReviewDecision.Replay
            |> Result.defaultWith LedgerReviewDecision.Fail

    let execute
        installationId
        operationId
        (subject: LedgerEnvelopeScope<_, _, _>)
        (head: LedgerHead)
        fenced
        nonce
        read
        =
        match subject.Admit (subject.Canonical()) read with
        | IdentityDecision.Refuse refusal -> LedgerDecision.Refuse refusal
        | IdentityDecision.Fail fault -> LedgerDecision.Fail fault
        | IdentityDecision.Replay entry ->
            subject.Receipt read None entry
            |> Result.map LedgerDecision.Replay
            |> Result.defaultWith LedgerDecision.Fail
        | IdentityDecision.Fresh when fenced -> LedgerDecision.Refuse Refusal.OperationResolved
        | IdentityDecision.Fresh when head.Sequence = Revision.Maximum ->
            LedgerDecision.Refuse Refusal.LedgerExhausted
        | IdentityDecision.Fresh ->
            match subject.Propose read with
            | LedgerReviewDecision.Refuse refusal -> LedgerDecision.Refuse refusal
            | LedgerReviewDecision.Fail fault -> LedgerDecision.Fail fault
            | LedgerReviewDecision.Replay receipt -> LedgerDecision.Replay receipt
            | LedgerReviewDecision.Proposed proposal ->
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
                    LedgerDecision.Append(entry, plan.Projection, receipt))
                |> Result.defaultWith LedgerDecision.Fail
