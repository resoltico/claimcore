namespace ClaimCore.Contracts

open System
open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application
open HttpInputSupport

[<RequireQualifiedAccess>]
type ServiceInput =
    | Empty
    | Command of CommandDraft
    | CaseReference of string
    | CaseList of CaseListRequest
    | History of HistoryRequest
    | Operation of Guid
    | Steward of StewardshipRequest
    | Register of Guid * PrincipalKey
    | SetGrant of Guid * PrincipalKey * Role * GrantTarget * bool
    | SetEnabled of Guid * PrincipalKey * bool
    | AuthorityObserve of Guid

module internal ServiceInputs =
    let private text name values = required name values |> stringValue
    let private guid name values = text name values |> operationIdValue

    let private principal (element: JsonElement) =
        let values = properties element |> exactProperties [ "kind"; "issuer"; "subject" ]

        let create =
            match text "kind" values with
            | "HUMAN" -> PrincipalKey.human
            | "SERVICE" -> PrincipalKey.service
            | _ -> fail HttpInputProblem.InvalidPrincipal

        create (text "issuer" values) (text "subject" values)
        |> Result.defaultWith (fun _ -> fail HttpInputProblem.InvalidPrincipal)

    let private scope (element: JsonElement) =
        let values = properties element

        match text "type" values with
        | "INSTALLATION" ->
            exactProperties [ "type" ] values |> ignore
            GrantTarget.Installation
        | "CASE" ->
            exactProperties [ "type"; "caseReference" ] values |> ignore
            GrantTarget.CaseReference(text "caseReference" values)
        | _ -> fail HttpInputProblem.InvalidAction

    let private steward values =
        let action =
            match text "action" values with
            | "VOID" -> StewardshipAction.Void(text "reason" values)
            | "REINSTATE" -> StewardshipAction.Reinstate(text "reason" values)
            | "ERASE" ->
                if values.ContainsKey "reason" then
                    fail HttpInputProblem.UnknownProperty

                StewardshipAction.Erase
            | _ -> fail HttpInputProblem.InvalidAction

        ServiceInput.Steward
            {
                OperationId = guid "operationId" values
                CaseReference = text "caseReference" values
                ExpectedVersion = text "expectedRevision" values |> canonicalNonNegativeInt64
                Action = action
            }

    let private management endpoint values =
        let eventId = guid "eventId" values

        if endpoint = "authority.observe" then
            ServiceInput.AuthorityObserve eventId
        else
            let principal = required "principal" values |> principal

            match endpoint with
            | "authority.register" -> ServiceInput.Register(eventId, principal)
            | "authority.setEnabled" ->
                ServiceInput.SetEnabled(eventId, principal, required "enabled" values |> boolValue)
            | "authority.setGrant" ->
                let role =
                    text "role" values
                    |> Roles.tryParse
                    |> Option.defaultWith (fun () -> fail HttpInputProblem.InvalidRole)

                ServiceInput.SetGrant(
                    eventId,
                    principal,
                    role,
                    required "scope" values |> scope,
                    required "active" values |> boolValue
                )
            | _ -> fail HttpInputProblem.InvalidAction

    let read endpoint element =
        let values = properties element

        match endpoint with
        | "session"
        | "session.logout"
        | "definition" ->
            exactProperties [] values |> ignore
            ServiceInput.Empty
        | "command.preview"
        | "command.execute" -> HttpCommandInput.draft element |> ServiceInput.Command
        | "case.get" -> ServiceInput.CaseReference(text "caseReference" values)
        | "operation.observe" -> ServiceInput.Operation(guid "operationId" values)
        | "case.list" ->
            ServiceInput.CaseList
                {
                    AfterCursor = optionalString "cursor" values
                    Limit = required "limit" values |> integerValue
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
                    AfterCursor = optionalString "cursor" values
                    Limit = required "limit" values |> integerValue
                    Detail = detail
                }
        | "case.steward" -> steward values
        | _ -> management endpoint values
