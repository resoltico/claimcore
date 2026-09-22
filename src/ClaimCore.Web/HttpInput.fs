namespace ClaimCore.Web

open ClaimCore.Contracts

open ClaimCore.Application
open HttpInputSupport

/// Strict HTTP-v2 request decoding. The request schema comes from ClaimCore.Contracts; these
/// entry points enforce exact JSON object semantics before values reach the typed Application facade.
module HttpInput =
    let readBounded = HttpInputSupport.readBounded

    let login bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "credential"; "antiforgeryToken" ]

            {
                Credential = required "credential" values |> stringValue
                AntiforgeryToken = required "antiforgeryToken" values |> stringValue
            })

    let logout bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties []

            if not values.IsEmpty then
                fail HttpInputProblem.LogoutShape)

    let draft bytes = parse bytes HttpCommandInput.draft

    let caseReference bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "caseReference" ]
            required "caseReference" values |> stringValue)

    let page maximum bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "cursor"; "limit" ]
            let limit = required "limit" values |> integerValue

            if limit < 1 || limit > maximum then
                fail HttpInputProblem.PageRange

            {
                Cursor = optionalString "cursor" values
                Limit = limit
            })

    let recoveryPage maximum bytes =
        parse bytes (HttpRecoveryInput.page maximum)

    let recoveryInspect maximum bytes =
        parse bytes (HttpRecoveryInput.inspect maximum)

    let history maximum bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties [ "caseReference"; "cursor"; "limit"; "detail" ]

            let limit = required "limit" values |> integerValue

            if limit < 1 || limit > maximum then
                fail HttpInputProblem.PageRange

            let detail =
                match required "detail" values |> stringValue with
                | "SUMMARY" -> HistoryDetail.Summary
                | "FULL" -> HistoryDetail.Full
                | _ -> fail HttpInputProblem.HistoryDetail

            {
                CaseReference = required "caseReference" values |> stringValue
                Cursor = optionalString "cursor" values
                Limit = limit
                Detail = detail
            })

    let operationId bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "operationId" ]
            required "operationId" values |> stringValue |> operationIdValue)

    let resolve bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "operationId"; "requestSha256" ]

            {
                OperationId = required "operationId" values |> stringValue |> operationIdValue
                RequestSha256 = required "requestSha256" values |> stringValue |> digestValue
            })

    let dismiss bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties [ "operationId"; "requestSha256"; "confirmed" ]

            {
                OperationId = required "operationId" values |> stringValue |> operationIdValue
                RequestSha256 = required "requestSha256" values |> stringValue |> digestValue
                Confirmed = required "confirmed" values |> boolValue
            })

    let sourceDigest = HttpInputSupport.sourceDigest
