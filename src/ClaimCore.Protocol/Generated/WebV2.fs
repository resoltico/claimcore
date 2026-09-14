// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

[<RequireQualifiedAccess>]
module WebV2 =
    let wireFingerprint =
        "a2c311c629a7429e134ff873e02d3a0541485d22cd1d8c487c06c25c9eb529d0"

    let hostFailureStatuses = [ 400; 401; 403; 404; 409; 413; 415; 429; 500; 503 ]

    let hostFailure =
        JsonCodec<HostFailure>((HostFailureJson.read), (HostFailureJson.write))

    let session = EndpointBinding0.value
    let sessionLogin = EndpointBinding1.value
    let sessionLogout = EndpointBinding2.value
    let definition = EndpointBinding3.value
    let caseGet = EndpointBinding4.value
    let caseList = EndpointBinding5.value
    let caseHistory = EndpointBinding6.value
    let operationObserve = EndpointBinding7.value
    let commandPrepare = EndpointBinding8.value
    let commandExecute = EndpointBinding9.value
    let recoveryList = EndpointBinding10.value
    let recoveryInspect = EndpointBinding11.value
    let recoveryResolve = EndpointBinding12.value
    let recoveryDismiss = EndpointBinding13.value
    let recoveryExport = EndpointBinding14.value
    let recoveryImportEnvelopePreview = EndpointBinding15.value
    let recoveryImportEnvelopeRetain = EndpointBinding16.value
    let recoveryImportRecordPreview = EndpointBinding17.value
    let recoveryImportRecordRetain = EndpointBinding18.value

    let endpoints =
        [
            session.Description
            sessionLogin.Description
            sessionLogout.Description
            definition.Description
            caseGet.Description
            caseList.Description
            caseHistory.Description
            operationObserve.Description
            commandPrepare.Description
            commandExecute.Description
            recoveryList.Description
            recoveryInspect.Description
            recoveryResolve.Description
            recoveryDismiss.Description
            recoveryExport.Description
            recoveryImportEnvelopePreview.Description
            recoveryImportEnvelopeRetain.Description
            recoveryImportRecordPreview.Description
            recoveryImportRecordRetain.Description
        ]
