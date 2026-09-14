// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal EndpointBinding0 =
    let value =
        ReadEndpoint(
            {
                Identifier = "session"
                Method = "GET"
                Path = "/api/v2/session"
                Body = RequestBody.None
                SuccessMediaType = None
            },
            JsonCodec<SessionResponse>((SessionResponseJson.read), (SessionResponseJson.write))
        )

module internal EndpointBinding1 =
    let value =
        JsonEndpoint(
            {
                Identifier = "session.login"
                Method = "POST"
                Path = "/api/v2/session/login"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<SessionLoginRequest>(
                (SessionLoginRequestJson.read),
                (SessionLoginRequestJson.write)
            ),
            JsonCodec<SessionLoginResponse>(
                (SessionLoginResponseJson.read),
                (SessionLoginResponseJson.write)
            )
        )

module internal EndpointBinding2 =
    let value =
        JsonEndpoint(
            {
                Identifier = "session.logout"
                Method = "POST"
                Path = "/api/v2/session/logout"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<SessionLogoutRequest>(
                (SessionLogoutRequestJson.read),
                (SessionLogoutRequestJson.write)
            ),
            JsonCodec<SessionLogoutResponse>(
                (SessionLogoutResponseJson.read),
                (SessionLogoutResponseJson.write)
            )
        )

module internal EndpointBinding3 =
    let value =
        ReadEndpoint(
            {
                Identifier = "definition"
                Method = "GET"
                Path = "/api/v2/definition"
                Body = RequestBody.None
                SuccessMediaType = None
            },
            JsonCodec<DefinitionResponse>(
                (DefinitionResponseJson.read),
                (DefinitionResponseJson.write)
            )
        )

module internal EndpointBinding4 =
    let value =
        JsonEndpoint(
            {
                Identifier = "case.get"
                Method = "POST"
                Path = "/api/v2/cases/get"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CaseGetRequest>((CaseGetRequestJson.read), (CaseGetRequestJson.write)),
            JsonCodec<CaseGetResponse>((CaseGetResponseJson.read), (CaseGetResponseJson.write))
        )

module internal EndpointBinding5 =
    let value =
        JsonEndpoint(
            {
                Identifier = "case.list"
                Method = "POST"
                Path = "/api/v2/cases/list"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CaseListRequest>((CaseListRequestJson.read), (CaseListRequestJson.write)),
            JsonCodec<CaseListResponse>((CaseListResponseJson.read), (CaseListResponseJson.write))
        )

module internal EndpointBinding6 =
    let value =
        JsonEndpoint(
            {
                Identifier = "case.history"
                Method = "POST"
                Path = "/api/v2/cases/history"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CaseHistoryRequest>(
                (CaseHistoryRequestJson.read),
                (CaseHistoryRequestJson.write)
            ),
            JsonCodec<CaseHistoryResponse>(
                (CaseHistoryResponseJson.read),
                (CaseHistoryResponseJson.write)
            )
        )

module internal EndpointBinding7 =
    let value =
        JsonEndpoint(
            {
                Identifier = "operation.observe"
                Method = "POST"
                Path = "/api/v2/operations/observe"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<OperationObserveRequest>(
                (OperationObserveRequestJson.read),
                (OperationObserveRequestJson.write)
            ),
            JsonCodec<OperationObserveResponse>(
                (OperationObserveResponseJson.read),
                (OperationObserveResponseJson.write)
            )
        )

module internal EndpointBinding8 =
    let value =
        JsonEndpoint(
            {
                Identifier = "command.prepare"
                Method = "POST"
                Path = "/api/v2/operations/prepare"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CommandPrepareRequest>(
                (CommandPrepareRequestJson.read),
                (CommandPrepareRequestJson.write)
            ),
            JsonCodec<CommandPrepareResponse>(
                (CommandPrepareResponseJson.read),
                (CommandPrepareResponseJson.write)
            )
        )

module internal EndpointBinding9 =
    let value =
        JsonEndpoint(
            {
                Identifier = "command.execute"
                Method = "POST"
                Path = "/api/v2/operations/submit"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CommandExecuteRequest>(
                (CommandExecuteRequestJson.read),
                (CommandExecuteRequestJson.write)
            ),
            JsonCodec<CommandExecuteResponse>(
                (CommandExecuteResponseJson.read),
                (CommandExecuteResponseJson.write)
            )
        )

module internal EndpointBinding10 =
    let value =
        JsonEndpoint(
            {
                Identifier = "recovery.list"
                Method = "POST"
                Path = "/api/v2/recovery/list"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CaseListRequest>((CaseListRequestJson.read), (CaseListRequestJson.write)),
            JsonCodec<RecoveryListResponse>(
                (RecoveryListResponseJson.read),
                (RecoveryListResponseJson.write)
            )
        )

module internal EndpointBinding11 =
    let value =
        JsonEndpoint(
            {
                Identifier = "recovery.inspect"
                Method = "POST"
                Path = "/api/v2/recovery/inspect"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<OperationObserveRequest>(
                (OperationObserveRequestJson.read),
                (OperationObserveRequestJson.write)
            ),
            JsonCodec<RecoveryInspectResponse>(
                (RecoveryInspectResponseJson.read),
                (RecoveryInspectResponseJson.write)
            )
        )

module internal EndpointBinding12 =
    let value =
        JsonEndpoint(
            {
                Identifier = "recovery.resolve"
                Method = "POST"
                Path = "/api/v2/recovery/resolve"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<CommandExecuteRequest>(
                (CommandExecuteRequestJson.read),
                (CommandExecuteRequestJson.write)
            ),
            JsonCodec<RecoveryResolveResponse>(
                (RecoveryResolveResponseJson.read),
                (RecoveryResolveResponseJson.write)
            )
        )

module internal EndpointBinding13 =
    let value =
        JsonEndpoint(
            {
                Identifier = "recovery.dismiss"
                Method = "POST"
                Path = "/api/v2/recovery/dismiss"
                Body = RequestBody.Json
                SuccessMediaType = None
            },
            JsonCodec<RecoveryDismissRequest>(
                (RecoveryDismissRequestJson.read),
                (RecoveryDismissRequestJson.write)
            ),
            JsonCodec<RecoveryDismissResponse>(
                (RecoveryDismissResponseJson.read),
                (RecoveryDismissResponseJson.write)
            )
        )

module internal EndpointBinding14 =
    let value =
        JsonEndpoint(
            {
                Identifier = "recovery.export"
                Method = "POST"
                Path = "/api/v2/recovery/export"
                Body = RequestBody.Json
                SuccessMediaType = (Some "application/vnd.claimcore.recovery\u002Bjson")
            },
            JsonCodec<CommandExecuteRequest>(
                (CommandExecuteRequestJson.read),
                (CommandExecuteRequestJson.write)
            ),
            JsonCodec<RecoveryExportResponse>(
                (RecoveryExportResponseJson.read),
                (RecoveryExportResponseJson.write)
            )
        )
