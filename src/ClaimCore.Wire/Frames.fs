namespace ClaimCore.Wire

open ClaimCore.Catalog

open System
open System.Text

[<RequireQualifiedAccess>]
type LocalFailure =
    | ConfigurationInvalid
    | AuthenticationFailed
    | ServiceUnavailable
    | Cancelled
    | Internal

[<RequireQualifiedAccess>]
type ProtocolFailure =
    | InvalidFrame
    | InvalidResponse
    | ContractMismatch

type FrameDelivery =
    {
        OperationId: Guid option
        AttemptKnowledge: AttemptKnowledge
        ResolutionEvidence: ResolutionEvidence
    }

[<RequireQualifiedAccess>]
type FrameReply =
    | Session of FrameDelivery * SessionOutcome
    | SessionLogout of FrameDelivery * SessionLogoutOutcome
    | Context of FrameDelivery * ContextOutcome
    | CaseGet of FrameDelivery * CaseGetOutcome
    | CaseList of FrameDelivery * CaseListOutcome
    | CaseHistory of FrameDelivery * CaseHistoryOutcome
    | CasePreview of FrameDelivery * CasePreviewOutcome
    | CaseExecute of FrameDelivery * CaseExecuteOutcome
    | OperationObserve of FrameDelivery * OperationObserveOutcome
    | AuthorityList of FrameDelivery * AuthorityListOutcome
    | AuthorityGrants of FrameDelivery * AuthorityGrantsOutcome
    | AuthorityExecute of FrameDelivery * AuthorityExecuteOutcome
    | ServiceFailure of FrameDelivery * Endpoint * HostFailureReply
    | LocalFailure of FrameDelivery * endpoint: string option * LocalFailure
    | ProtocolFailure of FrameDelivery * endpoint: string option * ProtocolFailure

[<NoEquality; NoComparison>]
type Invocation =
    private
        {
            Request: WireRequest
            InputBytes: byte array
        }

module Frames =
    open Codec
    open ResponseScalars

    let request (invocation: Invocation) = invocation.Request
    let inputBytes (invocation: Invocation) = Array.copy invocation.InputBytes

    let private invocationVariants0 =
        [
            variant
                (Endpoint.id Endpoint.Session)
                (fun () -> WireRequest.Session)
                (function
                | WireRequest.Session -> Some()
                | _ -> None)
                (record id |> field "input" id RequestCodecs.empty)
            variant
                (Endpoint.id Endpoint.SessionLogout)
                (fun () -> WireRequest.SessionLogout)
                (function
                | WireRequest.SessionLogout -> Some()
                | _ -> None)
                (record id |> field "input" id RequestCodecs.empty)
            variant
                (Endpoint.id Endpoint.Context)
                (fun () -> WireRequest.Context)
                (function
                | WireRequest.Context -> Some()
                | _ -> None)
                (record id |> field "input" id RequestCodecs.empty)
            variant
                (Endpoint.id Endpoint.CaseGet)
                WireRequest.CaseGet
                (function
                | WireRequest.CaseGet value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.caseGetRequest)
            variant
                (Endpoint.id Endpoint.CaseList)
                WireRequest.CaseList
                (function
                | WireRequest.CaseList value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.pageRequest)
            variant
                (Endpoint.id Endpoint.CaseHistory)
                WireRequest.CaseHistory
                (function
                | WireRequest.CaseHistory value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.historyRequest)
        ]

    let private invocationVariants1 =
        [
            variant
                (Endpoint.id Endpoint.CasePreview)
                WireRequest.CasePreview
                (function
                | WireRequest.CasePreview value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.caseRequest)
            variant
                (Endpoint.id Endpoint.CaseExecute)
                WireRequest.CaseExecute
                (function
                | WireRequest.CaseExecute value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.caseRequest)
            variant
                (Endpoint.id Endpoint.OperationObserve)
                WireRequest.OperationObserve
                (function
                | WireRequest.OperationObserve value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.observeRequest)
            variant
                (Endpoint.id Endpoint.AuthorityList)
                WireRequest.AuthorityList
                (function
                | WireRequest.AuthorityList value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.pageRequest)
            variant
                (Endpoint.id Endpoint.AuthorityGrants)
                WireRequest.AuthorityGrants
                (function
                | WireRequest.AuthorityGrants value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.grantsRequest)
            variant
                (Endpoint.id Endpoint.AuthorityExecute)
                WireRequest.AuthorityExecute
                (function
                | WireRequest.AuthorityExecute value -> Some value
                | _ -> None)
                (record id |> field "input" id RequestCodecs.authorityRequest)
        ]

    let private invocationVariants = invocationVariants0 @ invocationVariants1

    let invocationCodec = tagged "Invocation" "endpoint" invocationVariants

    let admit bytes =
        Strict.parse Limits.MaximumInvocationBytes bytes
        |> Result.bind (fun document ->
            use document = document

            invocationCodec.Read "$" document.RootElement
            |> Result.bind (fun value ->
                let input =
                    Encoding.UTF8.GetBytes(document.RootElement.GetProperty("input").GetRawText())

                if input.Length > Limits.MaximumRequestBytes then
                    Error
                        {
                            Path = "$.input"
                            Reason = "request exceeds its byte limit"
                        }
                else
                    Ok { Request = value; InputBytes = input }))

    let knowledge = enumeration DeliveryTokens.knowledgeDefinitions
    let evidence = enumeration DeliveryTokens.evidenceDefinitions

    let private localCode =
        enumeration
            [
                LocalFailure.ConfigurationInvalid, "CLI_CONFIGURATION_INVALID"
                LocalFailure.AuthenticationFailed, "CLI_AUTHENTICATION_FAILED"
                LocalFailure.ServiceUnavailable, "CLI_SERVICE_UNAVAILABLE"
                LocalFailure.Cancelled, "CLI_CANCELLED"
                LocalFailure.Internal, "CLI_INTERNAL"
            ]

    let private protocolCode =
        enumeration
            [
                ProtocolFailure.InvalidFrame, "INVALID_FRAME"
                ProtocolFailure.InvalidResponse, "INVALID_RESPONSE"
                ProtocolFailure.ContractMismatch, "CONTRACT_MISMATCH"
            ]

    let private delivery operation attempt resolution =
        {
            OperationId = operation
            AttemptKnowledge = attempt
            ResolutionEvidence = resolution
        }

    let private delivered get fields =
        fields
        |> field "operationId" (get >> _.OperationId) (nullable uuid)
        |> field "attemptKnowledge" (get >> _.AttemptKnowledge) knowledge
        |> field "resolutionEvidence" (get >> _.ResolutionEvidence) evidence

    let private resultCodec endpoint outcome =
        record (fun result operation attempt resolution ->
            delivery operation attempt resolution, result)
        |> fixedField "kind" "result"
        |> fixedField "endpoint" (Endpoint.id endpoint)
        |> field "outcome" snd outcome
        |> delivered fst
        |> build

    let private results0 =
        [
            branch
                (fun (meta, value) -> FrameReply.Session(meta, value))
                (function
                | FrameReply.Session(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.Session ResponseCodecs.sessionOutcome)
            branch
                (fun (meta, value) -> FrameReply.SessionLogout(meta, value))
                (function
                | FrameReply.SessionLogout(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.SessionLogout ResponseCodecs.sessionLogoutOutcome)
            branch
                (fun (meta, value) -> FrameReply.Context(meta, value))
                (function
                | FrameReply.Context(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.Context ResponseCodecs.contextOutcome)
            branch
                (fun (meta, value) -> FrameReply.CaseGet(meta, value))
                (function
                | FrameReply.CaseGet(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.CaseGet ResponseCodecs.caseGetOutcome)
            branch
                (fun (meta, value) -> FrameReply.CaseList(meta, value))
                (function
                | FrameReply.CaseList(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.CaseList ResponseCodecs.caseListOutcome)
            branch
                (fun (meta, value) -> FrameReply.CaseHistory(meta, value))
                (function
                | FrameReply.CaseHistory(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.CaseHistory ResponseCodecs.caseHistoryOutcome)
        ]

    let private results1 =
        [
            branch
                (fun (meta, value) -> FrameReply.CasePreview(meta, value))
                (function
                | FrameReply.CasePreview(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.CasePreview ResponseCodecs.casePreviewOutcome)
            branch
                (fun (meta, value) -> FrameReply.CaseExecute(meta, value))
                (function
                | FrameReply.CaseExecute(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.CaseExecute ResponseCodecs.caseExecuteOutcome)
            branch
                (fun (meta, value) -> FrameReply.OperationObserve(meta, value))
                (function
                | FrameReply.OperationObserve(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.OperationObserve ResponseCodecs.operationObserveOutcome)
            branch
                (fun (meta, value) -> FrameReply.AuthorityList(meta, value))
                (function
                | FrameReply.AuthorityList(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.AuthorityList ResponseCodecs.authorityListOutcome)
            branch
                (fun (meta, value) -> FrameReply.AuthorityGrants(meta, value))
                (function
                | FrameReply.AuthorityGrants(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.AuthorityGrants ResponseCodecs.authorityGrantsOutcome)
            branch
                (fun (meta, value) -> FrameReply.AuthorityExecute(meta, value))
                (function
                | FrameReply.AuthorityExecute(meta, value) -> Some(meta, value)
                | _ -> None)
                (resultCodec Endpoint.AuthorityExecute ResponseCodecs.authorityExecuteOutcome)
        ]

    let private results = results0 @ results1

    let private serviceCodec endpoint =
        record (fun failure operation attempt resolution ->
            delivery operation attempt resolution, failure)
        |> fixedField "kind" "serviceFailure"
        |> fixedField "endpoint" (Endpoint.id endpoint)
        |> field "failure" snd HostFailures.codec
        |> delivered fst
        |> build

    let private services =
        Endpoint.all
        |> List.map (fun endpoint ->
            branch
                (fun (meta, failure) -> FrameReply.ServiceFailure(meta, endpoint, failure))
                (function
                | FrameReply.ServiceFailure(meta, actual, failure) when actual = endpoint ->
                    Some(meta, failure)
                | _ -> None)
                (serviceCodec endpoint))

    let private local =
        record (fun code endpoint operation attempt resolution ->
            delivery operation attempt resolution, endpoint, code)
        |> fixedField "kind" "localFailure"
        |> field "code" (fun (_, _, code) -> code) localCode
        |> field "endpoint" (fun (_, endpoint, _) -> endpoint) (nullable nonempty)
        |> delivered (fun (meta, _, _) -> meta)
        |> build
        |> branch
            (fun (meta, endpoint, code) -> FrameReply.LocalFailure(meta, endpoint, code))
            (function
             | FrameReply.LocalFailure(meta, endpoint, code) -> Some(meta, endpoint, code)
             | _ -> None)

    let private protocol =
        record (fun code endpoint operation attempt resolution ->
            delivery operation attempt resolution, endpoint, code)
        |> fixedField "kind" "protocolFailure"
        |> field "code" (fun (_, _, code) -> code) protocolCode
        |> field "endpoint" (fun (_, endpoint, _) -> endpoint) (nullable nonempty)
        |> delivered (fun (meta, _, _) -> meta)
        |> build
        |> branch
            (fun (meta, endpoint, code) -> FrameReply.ProtocolFailure(meta, endpoint, code))
            (function
             | FrameReply.ProtocolFailure(meta, endpoint, code) -> Some(meta, endpoint, code)
             | _ -> None)

    let replyCodec = choice "CliReply" (results @ services @ [ local; protocol ])

    let encode value =
        let bytes = Codec.encode replyCodec value

        if bytes.Length > Limits.MaximumResponseBytes then
            invalidArg (nameof value) "CLI reply exceeds its byte limit."

        bytes

    let decode bytes =
        Codec.decode Limits.MaximumResponseBytes replyCodec bytes

    let fromReply meta =
        function
        | WireReply.Session value -> FrameReply.Session(meta, value.Outcome)
        | WireReply.SessionLogout value -> FrameReply.SessionLogout(meta, value.Outcome)
        | WireReply.Context value -> FrameReply.Context(meta, value.Outcome)
        | WireReply.CaseGet value -> FrameReply.CaseGet(meta, value.Outcome)
        | WireReply.CaseList value -> FrameReply.CaseList(meta, value.Outcome)
        | WireReply.CaseHistory value -> FrameReply.CaseHistory(meta, value.Outcome)
        | WireReply.CasePreview value -> FrameReply.CasePreview(meta, value.Outcome)
        | WireReply.CaseExecute value -> FrameReply.CaseExecute(meta, value.Outcome)
        | WireReply.OperationObserve value -> FrameReply.OperationObserve(meta, value.Outcome)
        | WireReply.AuthorityList value -> FrameReply.AuthorityList(meta, value.Outcome)
        | WireReply.AuthorityGrants value -> FrameReply.AuthorityGrants(meta, value.Outcome)
        | WireReply.AuthorityExecute value -> FrameReply.AuthorityExecute(meta, value.Outcome)

    let private remainingAttempt =
        function
        | FrameReply.CaseGet(_, outcome) ->
            WireReply.CaseGet { Outcome = outcome } |> WireReply.attempt
        | FrameReply.CaseList(_, outcome) ->
            WireReply.CaseList { Outcome = outcome } |> WireReply.attempt
        | FrameReply.CaseHistory(_, outcome) ->
            WireReply.CaseHistory { Outcome = outcome } |> WireReply.attempt
        | FrameReply.CasePreview(meta, outcome) ->
            WireReply.CasePreview
                {
                    Outcome = outcome
                    OperationId = defaultArg meta.OperationId Guid.Empty
                }
            |> WireReply.attempt
        | FrameReply.CaseExecute(meta, outcome) ->
            WireReply.CaseExecute
                {
                    Outcome = outcome
                    OperationId = defaultArg meta.OperationId Guid.Empty
                }
            |> WireReply.attempt
        | FrameReply.OperationObserve(_, outcome) ->
            WireReply.OperationObserve { Outcome = outcome } |> WireReply.attempt
        | FrameReply.AuthorityList(_, outcome) ->
            WireReply.AuthorityList { Outcome = outcome } |> WireReply.attempt
        | FrameReply.AuthorityGrants(_, outcome) ->
            WireReply.AuthorityGrants { Outcome = outcome } |> WireReply.attempt
        | FrameReply.AuthorityExecute(meta, outcome) ->
            WireReply.AuthorityExecute
                {
                    Outcome = outcome
                    OperationId = defaultArg meta.OperationId Guid.Empty
                }
            |> WireReply.attempt
        | _ -> invalidArg "frame" "A result frame is required."

    let private resultAttempt =
        function
        | FrameReply.Session(_, outcome) ->
            WireReply.Session { Outcome = outcome } |> WireReply.attempt
        | FrameReply.SessionLogout(_, outcome) ->
            WireReply.SessionLogout { Outcome = outcome } |> WireReply.attempt
        | FrameReply.Context(_, outcome) ->
            WireReply.Context { Outcome = outcome } |> WireReply.attempt
        | frame -> remainingAttempt frame

    let private remainingMetadata =
        function
        | FrameReply.CasePreview(meta, _)
        | FrameReply.CaseExecute(meta, _)
        | FrameReply.OperationObserve(meta, _)
        | FrameReply.AuthorityList(meta, _)
        | FrameReply.AuthorityGrants(meta, _)
        | FrameReply.AuthorityExecute(meta, _)
        | FrameReply.ServiceFailure(meta, _, _)
        | FrameReply.LocalFailure(meta, _, _)
        | FrameReply.ProtocolFailure(meta, _, _) -> meta
        | _ -> invalidArg "frame" "Expected a remaining frame variant."

    let metadata =
        function
        | FrameReply.Session(meta, _)
        | FrameReply.SessionLogout(meta, _)
        | FrameReply.Context(meta, _)
        | FrameReply.CaseGet(meta, _)
        | FrameReply.CaseList(meta, _)
        | FrameReply.CaseHistory(meta, _) -> meta
        | frame -> remainingMetadata frame

    let private withRemainingMetadata meta =
        function
        | FrameReply.CasePreview(_, value) -> FrameReply.CasePreview(meta, value)
        | FrameReply.CaseExecute(_, value) -> FrameReply.CaseExecute(meta, value)
        | FrameReply.OperationObserve(_, value) -> FrameReply.OperationObserve(meta, value)
        | FrameReply.AuthorityList(_, value) -> FrameReply.AuthorityList(meta, value)
        | FrameReply.AuthorityGrants(_, value) -> FrameReply.AuthorityGrants(meta, value)
        | FrameReply.AuthorityExecute(_, value) -> FrameReply.AuthorityExecute(meta, value)
        | FrameReply.ServiceFailure(_, endpoint, value) ->
            FrameReply.ServiceFailure(meta, endpoint, value)
        | FrameReply.LocalFailure(_, endpoint, value) ->
            FrameReply.LocalFailure(meta, endpoint, value)
        | FrameReply.ProtocolFailure(_, endpoint, value) ->
            FrameReply.ProtocolFailure(meta, endpoint, value)
        | _ -> invalidArg "frame" "Expected a remaining frame variant."

    let withMetadata meta =
        function
        | FrameReply.Session(_, value) -> FrameReply.Session(meta, value)
        | FrameReply.SessionLogout(_, value) -> FrameReply.SessionLogout(meta, value)
        | FrameReply.Context(_, value) -> FrameReply.Context(meta, value)
        | FrameReply.CaseGet(_, value) -> FrameReply.CaseGet(meta, value)
        | FrameReply.CaseList(_, value) -> FrameReply.CaseList(meta, value)
        | FrameReply.CaseHistory(_, value) -> FrameReply.CaseHistory(meta, value)
        | frame -> withRemainingMetadata meta frame

    let exitCode frame =
        if (metadata frame).AttemptKnowledge = AttemptKnowledge.Unconfirmed then
            4
        else
            match frame with
            | FrameReply.ProtocolFailure _ -> 2
            | FrameReply.ServiceFailure _ -> 3
            | FrameReply.LocalFailure(_, _, LocalFailure.Cancelled) -> 130
            | FrameReply.LocalFailure(_, _, LocalFailure.Internal) -> 70
            | FrameReply.LocalFailure _ -> 3
            | _ ->
                match resultAttempt frame with
                | AttemptResult.Outcome(OutcomeTag.Rejected, _) -> 2
                | AttemptResult.Outcome(OutcomeTag.Failed, _) -> 3
                | AttemptResult.Outcome(OutcomeTag.Cancelled, _) -> 130
                | _ -> 0
