namespace ClaimCore.Web

open System
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts
open HttpInputSupport

[<NoEquality; NoComparison>]
type internal ManagementRegisterInput =
    {
        EventId: Guid
        Principal: PrincipalKey
    }

[<NoEquality; NoComparison>]
type internal ManagementGrantInput =
    {
        EventId: Guid
        Principal: PrincipalKey
        Role: Role
        Scope: GrantTarget
        Active: bool
    }

[<NoEquality; NoComparison>]
type internal ManagementEnabledInput =
    {
        EventId: Guid
        Principal: PrincipalKey
        Enabled: bool
    }

module internal HttpManagementInput =
    let private eventId values =
        required "eventId" values |> stringValue |> operationIdValue

    let private principal (element: JsonElement) =
        let values = properties element
        let kind = required "kind" values |> stringValue
        let issuer = required "issuer" values |> stringValue

        let admitted =
            match kind with
            | "HUMAN" ->
                exactProperties [ "kind"; "issuer"; "subject" ] values |> ignore
                required "subject" values |> stringValue |> PrincipalKey.human issuer
            | "SERVICE" ->
                exactProperties [ "kind"; "issuer"; "clientId" ] values |> ignore
                required "clientId" values |> stringValue |> PrincipalKey.service issuer
            | _ -> fail HttpInputProblem.InvalidJson

        match admitted with
        | Ok value -> value
        | Error _ -> fail HttpInputProblem.InvalidJson

    let private role (element: JsonElement) =
        match stringValue element with
        | "OWNER" -> Role.Owner
        | "CASE_READER" -> Role.CaseReader
        | "CASE_EDITOR" -> Role.CaseEditor
        | "RECOVERY_OPERATOR" -> Role.RecoveryOperator
        | "RECOVERY_EXPORTER" -> Role.RecoveryExporter
        | "AUDITOR_CUSTODIAN" -> Role.AuditorCustodian
        | "DATA_STEWARD" -> Role.DataSteward
        | _ -> fail HttpInputProblem.InvalidJson

    let private scope (element: JsonElement) =
        let values = properties element

        match required "kind" values |> stringValue with
        | "INSTALLATION" ->
            exactProperties [ "kind" ] values |> ignore
            GrantTarget.Installation
        | "CASE" ->
            exactProperties [ "kind"; "caseReference" ] values |> ignore
            let reference = required "caseReference" values |> stringValue

            if String.IsNullOrWhiteSpace reference then
                fail HttpInputProblem.InvalidJson

            GrantTarget.CaseReference reference
        | _ -> fail HttpInputProblem.InvalidJson

    let register bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "eventId"; "principal" ]

            {
                EventId = eventId values
                Principal = required "principal" values |> principal
            })

    let setGrant bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties [ "eventId"; "principal"; "role"; "scope"; "active" ]

            {
                EventId = eventId values
                Principal = required "principal" values |> principal
                Role = required "role" values |> role
                Scope = required "scope" values |> scope
                Active = required "active" values |> boolValue
            })

    let setEnabled bytes =
        parse bytes (fun root ->
            let values =
                properties root |> exactProperties [ "eventId"; "principal"; "enabled" ]

            {
                EventId = eventId values
                Principal = required "principal" values |> principal
                Enabled = required "enabled" values |> boolValue
            })

    let observe bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "eventId" ]
            eventId values)
