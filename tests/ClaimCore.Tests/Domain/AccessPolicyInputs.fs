module ClaimCore.Tests.AccessPolicyInputs

open System
open Expecto
open ClaimCore.Domain

let private accepted result =
    result |> Result.defaultWith (fun error -> failtestf "%A" error)

let internal human =
    PrincipalKey.human "https://issuer.example/" "worker" |> accepted

let internal service =
    PrincipalKey.service "https://issuer.example/" "worker" |> accepted

let internal caseId =
    Guid.Parse "c0000000-0000-4000-8000-000000000001" |> CaseId.create |> Option.get

let internal otherCaseId =
    Guid.Parse "c0000000-0000-4000-8000-000000000002" |> CaseId.create |> Option.get

let internal actor principal grants : ActorAccess =
    {
        Actor =
            {
                ActorId =
                    (ActorId.create (Guid.Parse "a0000000-0000-4000-8000-000000000001")
                     |> Option.get)
                Principal = principal
                Enabled = true
                Profile = None
            }
        Grants = grants
    }

let internal expectedRoles =
    [
        Role.Owner, "OWNER", [ Capability.ManageAccess; Capability.ReadAccess ]
        Role.CaseReader, "CASE_READER", [ Capability.ReadCase; Capability.ReadHistory ]
        Role.CaseRegistrar,
        "CASE_REGISTRAR",
        [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
        Role.CaseHandler,
        "CASE_HANDLER",
        [
            Capability.ReadCase
            Capability.ReadHistory
            Capability.ReadHistoryFull
            Capability.Register
            Capability.Decide
            Capability.Close
            Capability.ManageCaseWork
        ]
        Role.PaymentRecorder,
        "PAYMENT_RECORDER",
        [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
        Role.DataSteward,
        "DATA_STEWARD",
        [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
        Role.Auditor,
        "AUDITOR",
        [
            Capability.ReadCase
            Capability.ReadHistory
            Capability.ReadHistoryFull
            Capability.ReadAccess
            Capability.ReadDisclosureTrail
        ]
    ]

let internal humanRoles = [ Role.Owner; Role.DataSteward ]

let internal allCapabilities =
    [
        Capability.ReadCase
        Capability.ReadHistory
        Capability.ReadHistoryFull
        Capability.ManageCaseWork
        Capability.Register
        Capability.Decide
        Capability.RecordPayment
        Capability.Close
        Capability.Steward
        Capability.ManageAccess
        Capability.ReadAccess
        Capability.ReadDisclosureTrail
    ]

type internal PermissionRule =
    {
        Role: Role
        Capabilities: Capability list
    }

let internal expectedPermission (rule: PermissionRule) principal grant resource capability =
    let role = rule.Role
    let capabilities = rule.Capabilities
    let humanRole = List.contains role humanRoles

    let installationOnly =
        List.contains
            capability
            [
                Capability.ManageAccess
                Capability.ReadAccess
                Capability.ReadDisclosureTrail
            ]

    let scope =
        match grant, resource with
        | Scope.Installation, Scope.Installation -> true
        | Scope.Installation, Scope.Case _ -> not installationOnly
        | Scope.Case granted, Scope.Case requested -> granted = requested && not installationOnly
        | _ -> false

    List.contains capability capabilities
    && scope
    && (not humanRole || principal = human)
    && (capability <> Capability.ManageCaseWork || principal = human)
