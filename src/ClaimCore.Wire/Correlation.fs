namespace ClaimCore.Wire

open System

/// Only identity-bearing values participate; presentation and payload bytes cannot affect correlation.
[<RequireQualifiedAccess>]
type RequestIdentity =
    | Case of operationId: Guid * reference: string * expectedRevision: int64 * act: string
    | Authority of operationId: Guid * act: string
    | Actor of Guid
    | Operation of Guid
    | Read

type ReceiptIdentity =
    {
        OperationId: Guid
        Scope: ReceiptScope
        Act: string
        CaseReference: string option
        Revision: int64 option
    }

[<RequireQualifiedAccess>]
type ReplyIdentity =
    | Receipt of envelopeOperation: Guid option * ReceiptIdentity
    | Reviewed of envelopeOperation: Guid * reference: string * revision: int64
    | Unknown of envelopeOperation: Guid * operationId: Guid
    | Actor of Guid
    | Observed of Guid
    | Other of envelopeOperation: Guid option

module Correlation =
    let private sameReference left right =
        String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private successor expected actual =
        expected < Strict.MaximumSafeInteger && actual = expected + 1L

    let private receipt request envelope (receipt: ReceiptIdentity) =
        match request with
        | RequestIdentity.Case(operation, reference, revision, act) ->
            envelope = Some operation
            && receipt.OperationId = operation
            && receipt.Scope = ReceiptScope.Case
            && receipt.Act = act
            && (receipt.CaseReference |> Option.exists (sameReference reference))
            && (receipt.Revision |> Option.exists (successor revision))
        | RequestIdentity.Authority(operation, act) ->
            envelope = Some operation
            && receipt.OperationId = operation
            && receipt.Scope = ReceiptScope.Authority
            && receipt.Act = act
        | _ -> false

    let private reviewed request envelope reference revision =
        match request with
        | RequestIdentity.Case(operation, expectedReference, expectedRevision, _) ->
            envelope = operation
            && sameReference reference expectedReference
            && successor expectedRevision revision
        | _ -> false

    let private unknown request envelope operation =
        match request with
        | RequestIdentity.Case(expected, _, _, _)
        | RequestIdentity.Authority(expected, _) -> envelope = expected && operation = expected
        | _ -> false

    let private other request envelope =
        match request with
        | RequestIdentity.Case(operation, _, _, _)
        | RequestIdentity.Authority(operation, _) -> envelope = Some operation
        | _ -> true

    let matchesIdentity request =
        function
        | ReplyIdentity.Receipt(envelope, value) -> receipt request envelope value
        | ReplyIdentity.Reviewed(envelope, reference, revision) ->
            reviewed request envelope reference revision
        | ReplyIdentity.Unknown(envelope, operation) -> unknown request envelope operation
        | ReplyIdentity.Actor actor -> request = RequestIdentity.Actor actor
        | ReplyIdentity.Observed operation -> request = RequestIdentity.Operation operation
        | ReplyIdentity.Other envelope -> other request envelope

    let private caseAct =
        function
        | CaseAct.Open _ -> "OPEN"
        | CaseAct.AmendRegistration _ -> "AMEND_REGISTRATION"
        | CaseAct.CorrectCase _ -> "CORRECT_CASE"
        | CaseAct.Decide _ -> "DECIDE"
        | CaseAct.WithdrawDecision -> "WITHDRAW_DECISION"
        | CaseAct.RecordPayment _ -> "RECORD_PAYMENT"
        | CaseAct.ClearPayment -> "CLEAR_PAYMENT"
        | CaseAct.Close -> "CLOSE"
        | CaseAct.Reopen -> "REOPEN"
        | CaseAct.Void _ -> "VOID"
        | CaseAct.Reinstate _ -> "REINSTATE"
        | CaseAct.Erase -> "ERASE"

    let requestIdentity =
        function
        | WireRequest.CasePreview value
        | WireRequest.CaseExecute value ->
            RequestIdentity.Case(
                value.OperationId,
                value.CaseReference,
                value.ExpectedRevision,
                caseAct value.Act
            )
        | WireRequest.AuthorityExecute value ->
            let act =
                match value.Act with
                | AuthorityAct.RegisterActor -> "REGISTER_ACTOR"
                | AuthorityAct.SetGrant _ -> "SET_GRANT"
                | AuthorityAct.SetEnabled _ -> "SET_ENABLED"

            RequestIdentity.Authority(value.OperationId, act)
        | WireRequest.AuthorityGrants value -> RequestIdentity.Actor value.ActorId
        | WireRequest.OperationObserve value -> RequestIdentity.Operation value.OperationId
        | _ -> RequestIdentity.Read

    let private caseIdentity (value: CaseReceipt) =
        {
            OperationId = value.OperationId
            Scope = ReceiptScope.Case
            Act = value.Act
            CaseReference = Some value.CaseReference
            Revision = Some value.Revision
        }

    let private authorityIdentity (value: AuthorityReceipt) =
        {
            OperationId = value.OperationId
            Scope = ReceiptScope.Authority
            Act = value.Act
            CaseReference = None
            Revision = None
        }

    let private preview (value: CasePreviewReply) =
        match value.Outcome with
        | CasePreviewOutcome.Reviewed data ->
            ReplyIdentity.Reviewed(
                value.OperationId,
                data.Review.After.CaseReference,
                data.Review.After.Revision
            )
        | CasePreviewOutcome.AlreadyAccepted data ->
            ReplyIdentity.Receipt(Some value.OperationId, caseIdentity data)
        | _ -> ReplyIdentity.Other(Some value.OperationId)

    let private execute (value: CaseExecuteReply) =
        match value.Outcome with
        | CaseExecuteOutcome.Accepted data
        | CaseExecuteOutcome.AlreadyAccepted data ->
            ReplyIdentity.Receipt(Some value.OperationId, caseIdentity data)
        | CaseExecuteOutcome.OutcomeUnknown data ->
            ReplyIdentity.Unknown(value.OperationId, data.OperationId)
        | _ -> ReplyIdentity.Other(Some value.OperationId)

    let private authority (value: AuthorityExecuteReply) =
        match value.Outcome with
        | AuthorityExecuteOutcome.Accepted data
        | AuthorityExecuteOutcome.AlreadyAccepted data ->
            ReplyIdentity.Receipt(Some value.OperationId, authorityIdentity data)
        | AuthorityExecuteOutcome.OutcomeUnknown data ->
            ReplyIdentity.Unknown(value.OperationId, data.OperationId)
        | _ -> ReplyIdentity.Other(Some value.OperationId)

    let replyIdentity =
        function
        | WireReply.CasePreview value -> preview value
        | WireReply.CaseExecute value -> execute value
        | WireReply.AuthorityExecute value -> authority value
        | WireReply.AuthorityGrants value ->
            match value.Outcome with
            | AuthorityGrantsOutcome.Succeeded data -> ReplyIdentity.Actor data.Actor.ActorId
            | _ -> ReplyIdentity.Other None
        | WireReply.OperationObserve value ->
            match value.Outcome with
            | OperationObserveOutcome.Succeeded(Receipt.Case data) ->
                ReplyIdentity.Observed data.OperationId
            | OperationObserveOutcome.Succeeded(Receipt.Authority data) ->
                ReplyIdentity.Observed data.OperationId
            | _ -> ReplyIdentity.Other None
        | _ -> ReplyIdentity.Other None

    let matches request reply =
        WireRequest.endpoint request = WireReply.endpoint reply
        && matchesIdentity (requestIdentity request) (replyIdentity reply)
