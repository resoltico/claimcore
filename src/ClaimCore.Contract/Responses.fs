namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

open System

type Session =
    {
        Authenticated: bool
        ContractIdentity: string
        AntiforgeryToken: string option
    }

type OutcomeUnknown = { OperationId: Guid }

type Reviewed = { Review: AdvisoryReview }

[<RequireQualifiedAccess>]
type SessionOutcome = Succeeded of Session

type SessionReply = { Outcome: SessionOutcome }

[<RequireQualifiedAccess>]
type SessionLogoutOutcome = Succeeded of unit

type SessionLogoutReply = { Outcome: SessionLogoutOutcome }

[<RequireQualifiedAccess>]
type ContextOutcome =
    | Succeeded of ActorContext
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type ContextReply = { Outcome: ContextOutcome }

[<RequireQualifiedAccess>]
type CaseGetOutcome =
    | Succeeded of CaseReadModel
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type CaseGetReply = { Outcome: CaseGetOutcome }

[<RequireQualifiedAccess>]
type CaseListOutcome =
    | Succeeded of CaseSummaryPage
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type CaseListReply = { Outcome: CaseListOutcome }

[<RequireQualifiedAccess>]
type CaseHistoryOutcome =
    | Succeeded of HistoryResultPage
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type CaseHistoryReply = { Outcome: CaseHistoryOutcome }

[<RequireQualifiedAccess>]
type CasePreviewOutcome =
    | Reviewed of Reviewed
    | AlreadyAccepted of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type CasePreviewReply =
    {
        OperationId: Guid
        Outcome: CasePreviewOutcome
    }

[<RequireQualifiedAccess>]
type CaseExecuteOutcome =
    | Accepted of OperationReceipt
    | AlreadyAccepted of OperationReceipt
    | OutcomeUnknown of OutcomeUnknown
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type CaseExecuteReply =
    {
        OperationId: Guid
        Outcome: CaseExecuteOutcome
    }

[<RequireQualifiedAccess>]
type OperationObserveOutcome =
    | Succeeded of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type OperationObserveReply = { Outcome: OperationObserveOutcome }

[<RequireQualifiedAccess>]
type AuthorityListOutcome =
    | Succeeded of ActorReviewPage
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type AuthorityListReply = { Outcome: AuthorityListOutcome }

[<RequireQualifiedAccess>]
type AuthorityGrantsOutcome =
    | Succeeded of AuthorityGrantPage
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type AuthorityGrantsReply = { Outcome: AuthorityGrantsOutcome }

[<RequireQualifiedAccess>]
type AuthorityExecuteOutcome =
    | Accepted of OperationReceipt
    | AlreadyAccepted of OperationReceipt
    | OutcomeUnknown of OutcomeUnknown
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled of unit

type AuthorityExecuteReply =
    {
        OperationId: Guid
        Outcome: AuthorityExecuteOutcome
    }

[<RequireQualifiedAccess>]
type WireReply =
    | Session of SessionReply
    | SessionLogout of SessionLogoutReply
    | Context of ContextReply
    | CaseGet of CaseGetReply
    | CaseList of CaseListReply
    | CaseHistory of CaseHistoryReply
    | CasePreview of CasePreviewReply
    | CaseExecute of CaseExecuteReply
    | OperationObserve of OperationObserveReply
    | AuthorityList of AuthorityListReply
    | AuthorityGrants of AuthorityGrantsReply
    | AuthorityExecute of AuthorityExecuteReply

module WireReply =
    let endpoint =
        function
        | WireReply.Session _ -> Endpoint.Session
        | WireReply.SessionLogout _ -> Endpoint.SessionLogout
        | WireReply.Context _ -> Endpoint.Context
        | WireReply.CaseGet _ -> Endpoint.CaseGet
        | WireReply.CaseList _ -> Endpoint.CaseList
        | WireReply.CaseHistory _ -> Endpoint.CaseHistory
        | WireReply.CasePreview _ -> Endpoint.CasePreview
        | WireReply.CaseExecute _ -> Endpoint.CaseExecute
        | WireReply.OperationObserve _ -> Endpoint.OperationObserve
        | WireReply.AuthorityList _ -> Endpoint.AuthorityList
        | WireReply.AuthorityGrants _ -> Endpoint.AuthorityGrants
        | WireReply.AuthorityExecute _ -> Endpoint.AuthorityExecute

    let operationId =
        function
        | WireReply.CasePreview value -> Some value.OperationId
        | WireReply.CaseExecute value -> Some value.OperationId
        | WireReply.AuthorityExecute value -> Some value.OperationId
        | _ -> None

    let private sessionAttempt (reply: SessionReply) =
        match reply.Outcome with
        | SessionOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)

    let private sessionLogoutAttempt (reply: SessionLogoutReply) =
        match reply.Outcome with
        | SessionLogoutOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)

    let private contextAttempt (reply: ContextReply) =
        match reply.Outcome with
        | ContextOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | ContextOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | ContextOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | ContextOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private caseGetAttempt (reply: CaseGetReply) =
        match reply.Outcome with
        | CaseGetOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | CaseGetOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | CaseGetOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | CaseGetOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private caseListAttempt (reply: CaseListReply) =
        match reply.Outcome with
        | CaseListOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | CaseListOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | CaseListOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | CaseListOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private caseHistoryAttempt (reply: CaseHistoryReply) =
        match reply.Outcome with
        | CaseHistoryOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | CaseHistoryOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | CaseHistoryOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | CaseHistoryOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private casePreviewAttempt (reply: CasePreviewReply) =
        match reply.Outcome with
        | CasePreviewOutcome.Reviewed _ -> AttemptResult.Outcome(OutcomeTag.Reviewed, None)
        | CasePreviewOutcome.AlreadyAccepted _ ->
            AttemptResult.Outcome(OutcomeTag.AlreadyAccepted, None)
        | CasePreviewOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | CasePreviewOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | CasePreviewOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private caseExecuteAttempt (reply: CaseExecuteReply) =
        match reply.Outcome with
        | CaseExecuteOutcome.Accepted _ -> AttemptResult.Outcome(OutcomeTag.Accepted, None)
        | CaseExecuteOutcome.AlreadyAccepted _ ->
            AttemptResult.Outcome(OutcomeTag.AlreadyAccepted, None)
        | CaseExecuteOutcome.OutcomeUnknown _ ->
            AttemptResult.Outcome(OutcomeTag.OutcomeUnknown, None)
        | CaseExecuteOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | CaseExecuteOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | CaseExecuteOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private operationObserveAttempt (reply: OperationObserveReply) =
        match reply.Outcome with
        | OperationObserveOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | OperationObserveOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | OperationObserveOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | OperationObserveOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private authorityListAttempt (reply: AuthorityListReply) =
        match reply.Outcome with
        | AuthorityListOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | AuthorityListOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | AuthorityListOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | AuthorityListOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private authorityGrantsAttempt (reply: AuthorityGrantsReply) =
        match reply.Outcome with
        | AuthorityGrantsOutcome.Succeeded _ -> AttemptResult.Outcome(OutcomeTag.Succeeded, None)
        | AuthorityGrantsOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | AuthorityGrantsOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | AuthorityGrantsOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let private authorityExecuteAttempt (reply: AuthorityExecuteReply) =
        match reply.Outcome with
        | AuthorityExecuteOutcome.Accepted _ -> AttemptResult.Outcome(OutcomeTag.Accepted, None)
        | AuthorityExecuteOutcome.AlreadyAccepted _ ->
            AttemptResult.Outcome(OutcomeTag.AlreadyAccepted, None)
        | AuthorityExecuteOutcome.OutcomeUnknown _ ->
            AttemptResult.Outcome(OutcomeTag.OutcomeUnknown, None)
        | AuthorityExecuteOutcome.Rejected value ->
            let diagnostic =
                match value with
                | Refusal.IdempotencyConflict -> Some "OPERATION_CONTENT_CONFLICT"
                | _ -> None

            AttemptResult.Outcome(OutcomeTag.Rejected, diagnostic)
        | AuthorityExecuteOutcome.Failed _ -> AttemptResult.Outcome(OutcomeTag.Failed, None)
        | AuthorityExecuteOutcome.Cancelled _ -> AttemptResult.Outcome(OutcomeTag.Cancelled, None)

    let attempt =
        function
        | WireReply.Session value -> sessionAttempt value
        | WireReply.SessionLogout value -> sessionLogoutAttempt value
        | WireReply.Context value -> contextAttempt value
        | WireReply.CaseGet value -> caseGetAttempt value
        | WireReply.CaseList value -> caseListAttempt value
        | WireReply.CaseHistory value -> caseHistoryAttempt value
        | WireReply.CasePreview value -> casePreviewAttempt value
        | WireReply.CaseExecute value -> caseExecuteAttempt value
        | WireReply.OperationObserve value -> operationObserveAttempt value
        | WireReply.AuthorityList value -> authorityListAttempt value
        | WireReply.AuthorityGrants value -> authorityGrantsAttempt value
        | WireReply.AuthorityExecute value -> authorityExecuteAttempt value
