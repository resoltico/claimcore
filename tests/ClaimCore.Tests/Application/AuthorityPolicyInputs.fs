module ClaimCore.Tests.AuthorityPolicyInputs

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Tests.Fixtures


let internal human =
    PrincipalKey.human "https://issuer.example/" "worker" |> accepted

let internal service =
    PrincipalKey.service "https://issuer.example/" "worker" |> accepted

let internal caseId = Guid.Parse "c0000000-0000-4000-8000-000000000001"
let internal otherCaseId = Guid.Parse "c0000000-0000-4000-8000-000000000002"

let internal actor principal grants : ActorAuthority =
    {
        Actor =
            {
                ActorId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
                Principal = principal
                Enabled = true
            }
        Grants = grants
    }

let internal expectedRoles =
    [
        Role.Owner, "OWNER", [ Capability.ManageAuthority; Capability.ReadAuthority ]
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
            Capability.ReadAuthority
        ]
    ]

let internal allCapabilities =
    [
        Capability.ReadCase
        Capability.ReadHistory
        Capability.ReadHistoryFull
        Capability.Register
        Capability.Decide
        Capability.RecordPayment
        Capability.Close
        Capability.Steward
        Capability.ManageAuthority
        Capability.ReadAuthority
    ]

type internal PermissionRule =
    {
        Role: Role
        Capabilities: Capability list
    }

let internal expectedPermission (rule: PermissionRule) principal grant resource capability =
    let role = rule.Role
    let capabilities = rule.Capabilities
    let humanRole = role = Role.Owner || role = Role.DataSteward

    let installationOnly =
        capability = Capability.ManageAuthority || capability = Capability.ReadAuthority

    let scope =
        match grant, resource with
        | Scope.Installation, Scope.Installation -> true
        | Scope.Installation, Scope.Case id -> id <> Guid.Empty && not installationOnly
        | Scope.Case granted, Scope.Case requested ->
            granted = requested && requested <> Guid.Empty && not installationOnly
        | _ -> false

    List.contains capability capabilities
    && scope
    && (not humanRole || principal = human)
