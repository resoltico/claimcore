namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

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
    let private document value =
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes value
        |> fun bytes -> System.Text.Json.JsonDocument.Parse(ReadOnlyMemory<byte> bytes)

    let private authored =
        function
        | RequestIdentity.Case(operation, reference, revision, act) ->
            Endpoint.CaseExecute,
            box
                {|
                    operationId = operation
                    caseReference = reference
                    expectedRevision = revision
                    act = {| kind = act |}
                |}
        | RequestIdentity.Authority(operation, act) ->
            Endpoint.AuthorityExecute,
            box
                {|
                    operationId = operation
                    act = {| kind = act |}
                |}
        | RequestIdentity.Actor actor -> Endpoint.AuthorityGrants, box {| actorId = actor |}
        | RequestIdentity.Operation operation ->
            Endpoint.OperationObserve, box {| operationId = operation |}
        | RequestIdentity.Read -> Endpoint.Context, box {| empty = true |}

    let private receivedReceipt (envelope: Guid option) (receipt: ReceiptIdentity) =
        box
            {|
                operationId = envelope
                outcome =
                    {|
                        tag = "ACCEPTED"
                        data =
                            {|
                                operationId = receipt.OperationId
                                act = receipt.Act
                                scope =
                                    (match receipt.Scope with
                                     | ReceiptScope.Case -> "CASE"
                                     | ReceiptScope.Authority -> "AUTHORITY")
                                caseReference = receipt.CaseReference
                                revision = receipt.Revision
                            |}
                    |}
            |}

    let private receivedReview (envelope: Guid) (reference: string) (revision: int64) =
        box
            {|
                operationId = envelope
                outcome =
                    {|
                        tag = "REVIEWED"
                        data =
                            {|
                                review =
                                    {|
                                        after =
                                            {|
                                                caseReference = reference
                                                revision = revision
                                            |}
                                    |}
                            |}
                    |}
            |}

    let private received =
        function
        | ReplyIdentity.Receipt(envelope, receipt) -> receivedReceipt envelope receipt
        | ReplyIdentity.Reviewed(envelope, reference, revision) ->
            receivedReview envelope reference revision
        | ReplyIdentity.Unknown(envelope, operation) ->
            box
                {|
                    operationId = envelope
                    outcome =
                        {|
                            tag = "OUTCOME_UNKNOWN"
                            data = {| operationId = operation |}
                        |}
                |}
        | ReplyIdentity.Actor actor ->
            box
                {|
                    outcome =
                        {|
                            tag = "SUCCEEDED"
                            data = {| actor = {| actorId = actor |} |}
                        |}
                |}
        | ReplyIdentity.Observed operation ->
            box
                {|
                    outcome =
                        {|
                            tag = "SUCCEEDED"
                            data = {| operationId = operation |}
                        |}
                |}
        | ReplyIdentity.Other envelope ->
            box
                {|
                    operationId = envelope
                    outcome = {| tag = "REJECTED" |}
                |}

    let matchesIdentity request reply =
        let applicable =
            match reply, request with
            | ReplyIdentity.Actor _, RequestIdentity.Actor _
            | ReplyIdentity.Observed _, RequestIdentity.Operation _ -> true
            | ReplyIdentity.Actor _, _
            | ReplyIdentity.Observed _, _ -> false
            | _ -> true

        let sourceEndpoint, value = authored request

        let endpoint =
            if
                sourceEndpoint = Endpoint.CaseExecute
                && (match reply with
                    | ReplyIdentity.Reviewed _ -> true
                    | _ -> false)
            then
                Endpoint.CasePreview
            else
                sourceEndpoint

        use requestDocument = document value
        use replyDocument = document (received reply)

        applicable
        && CorrelationEvaluation.matches
            endpoint
            requestDocument.RootElement
            replyDocument.RootElement

    let private caseAct =
        function
        | CaseAct.Open _ -> (CaseActKinds.token (CaseActKind.Business BusinessActKind.Open))
        | CaseAct.AmendRegistration _ ->
            (CaseActKinds.token (CaseActKind.Business BusinessActKind.AmendRegistration))
        | CaseAct.CorrectCase _ ->
            (CaseActKinds.token (CaseActKind.Business BusinessActKind.CorrectCase))
        | CaseAct.Decide _ -> (CaseActKinds.token (CaseActKind.Business BusinessActKind.Decide))
        | CaseAct.WithdrawDecision ->
            (CaseActKinds.token (CaseActKind.Business BusinessActKind.WithdrawDecision))
        | CaseAct.RecordPayment _ ->
            (CaseActKinds.token (CaseActKind.Business BusinessActKind.RecordPayment))
        | CaseAct.ClearPayment ->
            (CaseActKinds.token (CaseActKind.Business BusinessActKind.ClearPayment))
        | CaseAct.Close -> (CaseActKinds.token (CaseActKind.Business BusinessActKind.Close))
        | CaseAct.Reopen -> (CaseActKinds.token (CaseActKind.Business BusinessActKind.Reopen))
        | CaseAct.Void _ -> (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Void))
        | CaseAct.Reinstate _ ->
            (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Reinstate))
        | CaseAct.Erase -> (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Erase))

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
                | AuthorityAct.RegisterActor -> (AuthorityKinds.token AuthorityKind.RegisterActor)
                | AuthorityAct.SetGrant _ -> (AuthorityKinds.token AuthorityKind.SetGrant)
                | AuthorityAct.SetEnabled _ -> (AuthorityKinds.token AuthorityKind.SetEnabled)

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
