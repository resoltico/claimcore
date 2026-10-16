namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System
open System.Text
open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open HttpInputSupport

[<RequireQualifiedAccess>]
type ServiceInput =
    | Empty
    | Case of CaseRequest
    | CaseReference of string
    | CaseList of CaseListRequest
    | History of HistoryRequest
    | Operation of Guid
    | Authority of AuthorityRequest
    | ActorList of AuthorityListRequest
    | ActorGrants of AuthorityGrantListRequest

module internal ServiceInputs =
    let private text name values = required name values |> stringValue

    let private raw (element: JsonElement) =
        Encoding.UTF8.GetBytes(element.GetRawText())

    let private decoded value =
        Result.defaultWith (fun _ -> fail HttpInputProblem.InvalidJson) value

    let private page endpoint values =
        let cursor = optionalString "cursor" values
        let limit () = required "limit" values |> integerValue

        match endpoint with
        | "case.list" ->
            ServiceInput.CaseList
                {
                    AfterCursor = cursor
                    Limit = limit ()
                }
        | "authority.list" ->
            ServiceInput.ActorList
                {
                    AfterCursor = cursor
                    Limit = limit ()
                }
        | "authority.grants" ->
            ServiceInput.ActorGrants
                {
                    ActorId = text "actorId" values |> operationIdValue
                    AfterCursor = cursor
                    Limit = limit ()
                }
        | "case.history" ->
            let detail =
                match text "detail" values with
                | "SUMMARY" -> HistoryDetail.Summary
                | "FULL" -> HistoryDetail.Full
                | _ -> fail HttpInputProblem.HistoryDetail

            ServiceInput.History
                {
                    CaseReference = text "caseReference" values
                    AfterCursor = cursor
                    Limit = limit ()
                    Detail = detail
                }
        | _ -> fail HttpInputProblem.InvalidAction

    let read endpoint element =
        let values = properties element

        match endpoint with
        | "session"
        | "session.logout"
        | "context" ->
            exactProperties [] values |> ignore
            ServiceInput.Empty
        | "case.preview"
        | "case.execute" ->
            RequestRecord.decodeWire ServiceLimits.RequestByteLimit (raw element)
            |> decoded
            |> ServiceInput.Case
        | "authority.execute" ->
            AuthorityRecord.decodeWire ServiceLimits.RequestByteLimit (raw element)
            |> decoded
            |> AuthorityRequests.fromRecord
            |> decoded
            |> ServiceInput.Authority
        | "case.get" -> ServiceInput.CaseReference(text "caseReference" values)
        | "operation.observe" ->
            ServiceInput.Operation(text "operationId" values |> operationIdValue)
        | _ -> page endpoint values
