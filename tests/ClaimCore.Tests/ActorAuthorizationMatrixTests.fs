module ClaimCore.Tests.ActorAuthorizationMatrixTests

open System
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Tests.ActorAuthorizationTests


let private endpointMatrix () =
    let actions = ActorAuthorization.allActions
    Expect.equal actions.Length 38 "Every semantic endpoint is in the reviewed matrix"
    Expect.equal (Set.ofList actions).Count actions.Length "No endpoint is repeated"

    Expect.equal
        (ActorAuthorization.requiredCapability EndpointAction.ApproveLifecycle)
        (Some Capability.ApproveLifecycle)
        "Disposition approval has its own capability"

    Expect.equal
        (ActorAuthorization.requiredCapability EndpointAction.ApproveWriterHandoff)
        (Some Capability.ApproveWriterHandoff)
        "Writer handoff approval has its own owner capability"

    Expect.equal
        (ActorAuthorization.requiredCapability EndpointAction.ApproveTerminalErasure)
        (Some Capability.ApproveTerminalErasure)
        "Terminal erasure approval has its own steward capability"

    Expect.equal
        (ActorAuthorization.requiredCapability EndpointAction.ApproveCopyAdoption)
        (Some Capability.ApproveCopyAdoption)
        "Managed-copy adoption approval has its own owner capability"

    let declared =
        FSharpType.GetUnionCases(typeof<EndpointAction>)
        |> Array.map (fun case ->
            FSharpValue.MakeUnion(case, [||])
            |> Option.ofObj
            |> Option.map unbox<EndpointAction>
            |> Option.defaultWith (fun () -> failtest "Endpoint union case could not be reflected"))
        |> Set.ofArray

    Expect.equal (Set.ofList actions) declared "Every declared endpoint has a matrix entry"

    actions
    |> List.iter (fun action ->
        Expect.isSome (ActorAuthorization.requiredCapability action) "Capability is explicit")

    let editor = actor [ grant Role.CaseEditor GrantScope.Installation ]
    let caseOnly = actor [ grant Role.CaseEditor (GrantScope.Case caseId) ]

    Expect.isTrue
        (ActorAuthorization.can
            editor.Principal
            editor
            EndpointAction.ExecuteNewCase
            ResourceScope.Installation)
        "Opening a new case requires installation edit scope"

    Expect.isFalse
        (ActorAuthorization.can
            caseOnly.Principal
            caseOnly
            EndpointAction.ExecuteNewCase
            ResourceScope.Installation)
        "A case-specific grant cannot create another case"

let private recoveryAndOperationScope () =
    let operationId = Guid.Parse("44444444-4444-4444-8444-444444444444")
    let operator = actor [ grant Role.RecoveryOperator (GrantScope.Case caseId) ]
    let exporter = actor [ grant Role.RecoveryExporter (GrantScope.Case caseId) ]
    let resource = ResourceScope.Operation(operationId, caseId)

    Expect.isTrue
        (ActorAuthorization.can operator.Principal operator EndpointAction.RecoveryResolve resource)
        "Recovery operator can resolve only its case operation"

    Expect.isFalse
        (ActorAuthorization.can operator.Principal operator EndpointAction.RecoveryExport resource)
        "Recovery execution does not imply export disclosure"

    Expect.isTrue
        (ActorAuthorization.can exporter.Principal exporter EndpointAction.RecoveryExport resource)
        "Export requires a separate explicit grant"

    Expect.isFalse
        (ActorAuthorization.can
            exporter.Principal
            exporter
            EndpointAction.RecoveryExport
            (ResourceScope.Operation(Guid.Empty, caseId)))
        "An invalid operation identity is never authorized"

    let unavailable =
        ActorAuthorization.authorizeOperationLookup
            exporter.Principal
            exporter
            EndpointAction.RecoveryExport
            None

    let hidden =
        ActorAuthorization.authorizeOperationLookup
            exporter.Principal
            exporter
            EndpointAction.RecoveryExport
            (Some(operationId, Guid.Parse("33333333-3333-4333-8333-333333333333")))

    Expect.equal unavailable hidden "Absent and inaccessible operations disclose identically"


let tests =
    testList
        "actor and grant authorization"
        [
            testCase
                "[CC-AUTH-001] every semantic endpoint has one scope and capability"
                endpointMatrix
            testCase
                "[CC-AUTH-001] operation access and export are separately granted"
                recoveryAndOperationScope
        ]
