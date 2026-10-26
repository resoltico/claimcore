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
        Scope: StoredScope
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

/// One sequencing rule for both ledgers; scoped rules receive the immutable read view.
module internal LedgerEnvelope =
    let actorId (view: AuthorityView) =
        view.Actor
        |> Option.map _.ActorId
        |> Option.defaultWith (fun () -> invalidArg (nameof view) "Authorized actor is missing.")

    let review (scope: LedgerEnvelopeScope<_, _, _>) read =
        match scope.Admit (scope.Canonical()) read with
        | IdentityDecision.Fresh -> scope.Propose read
        | IdentityDecision.Refuse refusal -> LedgerReviewDecision.Refuse refusal
        | IdentityDecision.Fail fault -> LedgerReviewDecision.Fail fault
        | IdentityDecision.Replay entry ->
            scope.Receipt read None entry
            |> Result.map LedgerReviewDecision.Replay
            |> Result.defaultWith LedgerReviewDecision.Fail

    let execute
        installationId
        operationId
        (scope: LedgerEnvelopeScope<_, _, _>)
        (head: LedgerHead)
        fenced
        nonce
        read
        =
        match scope.Admit (scope.Canonical()) read with
        | IdentityDecision.Refuse refusal -> LedgerDecision.Refuse refusal
        | IdentityDecision.Fail fault -> LedgerDecision.Fail fault
        | IdentityDecision.Replay entry ->
            scope.Receipt read None entry
            |> Result.map LedgerDecision.Replay
            |> Result.defaultWith LedgerDecision.Fail
        | IdentityDecision.Fresh when fenced -> LedgerDecision.Refuse Refusal.OperationResolved
        | IdentityDecision.Fresh when head.Sequence = Revision.Maximum ->
            LedgerDecision.Refuse Refusal.LedgerExhausted
        | IdentityDecision.Fresh ->
            match scope.Propose read with
            | LedgerReviewDecision.Refuse refusal -> LedgerDecision.Refuse refusal
            | LedgerReviewDecision.Fail fault -> LedgerDecision.Fail fault
            | LedgerReviewDecision.Replay receipt -> LedgerDecision.Replay receipt
            | LedgerReviewDecision.Proposed proposal ->
                let plan = scope.Project read proposal

                let entry =
                    LedgerEntries.create
                        installationId
                        head
                        plan.ObservedAt
                        nonce
                        plan.ActorId
                        operationId
                        plan.Scope
                        (scope.Encode())
                        plan.Snapshot

                LedgerEntries.validate installationId (LedgerEntries.stored entry)
                |> Result.bind (scope.Receipt read plan.Current)
                |> Result.map (fun receipt ->
                    LedgerDecision.Append(entry, plan.Projection, receipt))
                |> Result.defaultWith LedgerDecision.Fail
