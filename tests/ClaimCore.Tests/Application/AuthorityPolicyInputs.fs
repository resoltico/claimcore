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

let internal caseId =
    Guid.Parse "c0000000-0000-4000-8000-000000000001" |> CaseFixtures.caseId

let internal otherCaseId =
    Guid.Parse "c0000000-0000-4000-8000-000000000002" |> CaseFixtures.caseId

let internal actor principal grants : ActorAuthority =
    {
        Actor =
            {
                ActorId = (CaseFixtures.actorId (Guid.Parse "a0000000-0000-4000-8000-000000000001"))
                Principal = principal
                Enabled = true
            }
        Grants = grants
    }

let internal expectedRoles =
    Inventory.rows "roles"
    |> List.map (fun row ->
        let token = row.GetProperty("token").GetString() |> nonNull

        let capabilities =
            row.GetProperty("capabilities").EnumerateArray()
            |> Seq.map (fun value -> Capability.parse (value.GetString() |> nonNull) |> Option.get)
            |> Seq.toList

        Role.parse token |> Option.get, token, capabilities)

let internal humanRoles =
    Inventory.rows "roles"
    |> List.filter (fun row -> row.GetProperty("humanOnly").GetBoolean())
    |> List.map (fun row ->
        Role.parse (row.GetProperty("token").GetString() |> nonNull) |> Option.get)

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
    let humanRole = List.contains role humanRoles

    let installationOnly =
        capability = Capability.ManageAuthority || capability = Capability.ReadAuthority

    let scope =
        match grant, resource with
        | Scope.Installation, Scope.Installation -> true
        | Scope.Installation, Scope.Case _ -> not installationOnly
        | Scope.Case granted, Scope.Case requested -> granted = requested && not installationOnly
        | _ -> false

    List.contains capability capabilities
    && scope
    && (not humanRole || principal = human)
