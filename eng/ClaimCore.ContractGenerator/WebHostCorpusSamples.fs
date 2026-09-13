namespace ClaimCore.ContractGeneration

open ClaimCore.Contracts

[<NoEquality; NoComparison>]
type internal WebHostSample =
    {
        Identifier: string
        Status: int
        Code: string
        Phase: string option
        Bytes: byte array
    }

[<RequireQualifiedAccess>]
module internal WebHostCorpusSamples =
    let private sample identifier status code phase =
        {
            Identifier = identifier
            Status = status
            Code = code
            Phase = phase
            Bytes = WebWireCodec.hostFailure code "Synthetic safe host failure." phase
        }

    let all =
        [
            sample "invalid-request" 400 "WEB_INVALID_REQUEST" (Some "NOT_STARTED")
            sample "session-rejected" 401 "WEB_SESSION_REJECTED" None
            sample "login-rejected" 401 "WEB_LOGIN_REJECTED" (Some "NOT_STARTED")
            sample "connection-rejected" 403 "WEB_CONNECTION_REJECTED" None
            sample "origin-rejected" 403 "WEB_ORIGIN_REJECTED" None
            sample "csrf-rejected" 403 "WEB_CSRF_REJECTED" None
            sample "not-found" 404 "WEB_NOT_FOUND" None
            sample "synthetic-conflict" 409 "WEB_SYNTHETIC_CONFLICT" None
            sample "body-too-large" 413 "WEB_BODY_TOO_LARGE" None
            sample "media-type" 415 "WEB_MEDIA_TYPE" None
            sample "busy" 429 "WEB_BUSY" None
            sample "protocol" 500 "WEB_PROTOCOL" None
            sample
                "synthetic-unavailable"
                503
                "WEB_SYNTHETIC_UNAVAILABLE"
                (Some "STARTED_UNCONFIRMED")
        ]

    let assertComplete () =
        let statuses = all |> List.map _.Status |> Set.ofList
        let expectedStatuses = WebSchemaDefinitions.hostFailureStatuses |> Set.ofList

        if statuses <> expectedStatuses then
            invalidOp "Web host corpus must cover every generated allowed status."

        let phases = all |> List.map _.Phase |> Set.ofList

        if phases <> Set.ofList [ None; Some "NOT_STARTED"; Some "STARTED_UNCONFIRMED" ] then
            invalidOp "Web host corpus must cover every execution-phase branch."
