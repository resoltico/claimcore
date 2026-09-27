module ClaimCore.WebTests.RouteTests

open System
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures

let private getDelegation () =
    let runtime = RuntimeStub()
    let request = context validGet

    let output =
        Routes.get admit 65536 runtime.ActorCore request
        |> readResult
        |> execute request

    use document = JsonDocument.Parse(output)

    Expect.equal runtime.CoreCalls 1 "The case endpoint calls its one typed core method"
    Expect.equal runtime.RecoveryCalls 0 "Case lookup does not reach technical recovery"

    Expect.equal
        (document.RootElement.GetProperty("endpoint").GetString())
        "case.get"
        "The generic v1 query envelope is gone"

let private recoveryDelegation () =
    let runtime = RuntimeStub()
    let request = context validResolve

    let output =
        Routes.recoveryResolve admit 65536 runtime.ActorCore request
        |> readResult
        |> execute request

    use document = JsonDocument.Parse(output)

    Expect.equal runtime.CoreCalls 0 "The host does not execute or settle a retained command itself"
    Expect.equal runtime.RecoveryCalls 1 "The public core recovery workflow owns resolution"

    Expect.equal
        (document.RootElement.GetProperty("outcome").GetProperty("tag").GetString())
        "REFUSED_BEFORE_ATTEMPT"
        "Recovery lifecycle meaning is preserved in a typed outcome"

let private refusalBeforeCore () =
    let runtime = RuntimeStub()
    let request = context "{}"
    let result = Routes.get admit 65536 runtime.ActorCore request |> readResult
    execute request result |> ignore

    Expect.equal
        request.Response.StatusCode
        400
        "Malformed endpoint bodies are rejected at admission"

    Expect.equal runtime.CoreCalls 0 "Malformed input does not call the core"
    Expect.equal runtime.RecoveryCalls 0 "Malformed input cannot trigger recovery"

let private rawRetainHeader () =
    let runtime = RuntimeStub()
    let request = context "synthetic raw bytes"
    let _, _, maximumBytes, headers = WebContract.raw "recovery.importEnvelopeRetain"

    let result =
        Routes.envelopeRetain admit maximumBytes (List.exactlyOne headers) runtime.ActorCore request
        |> readResult

    execute request result |> ignore

    Expect.equal request.Response.StatusCode 400 "Retain requires the generated exact digest header"
    Expect.equal runtime.RecoveryCalls 0 "Missing source proof never reaches retention"

let private managementOutcomes () =
    let eventId = Guid.Parse("40000000-0000-4000-8000-000000000001")
    let target = Guid.Parse("50000000-0000-4000-8000-000000000001")
    let runtime = RuntimeStub()
    let input = $"""{{"eventId":"{eventId:D}"}}"""

    let outcome selected =
        runtime.ManagementOutcome <- Some selected
        let request = context input

        let output =
            Routes.managementObserve admit 65536 runtime.ActorCore request
            |> readResult
            |> execute request

        use document = JsonDocument.Parse(output)
        document.RootElement.GetProperty("outcome").Clone()

    let unavailable = outcome ActorManagementOutcome.ResourceUnavailable

    Expect.equal
        (unavailable.GetProperty("tag").GetString())
        "RESOURCE_UNAVAILABLE"
        "Unknown actor and inaccessible event are indistinguishable"

    Expect.equal
        (unavailable.GetProperty("data").ValueKind)
        JsonValueKind.Null
        "Refusal does not identify an actor"

    let unconfirmed = outcome (ActorManagementOutcome.Unconfirmed eventId)

    Expect.equal
        (unconfirmed.GetProperty("tag").GetString())
        "UNCONFIRMED"
        "Attempt uncertainty is explicit"

    Expect.equal
        (unconfirmed.GetProperty("data").GetProperty("eventId").GetGuid())
        eventId
        "Original caller event ID survives"

    let applied = outcome (ActorManagementOutcome.Applied(eventId, 3L, target))

    Expect.equal
        (applied.GetProperty("tag").GetString())
        "APPLIED"
        "Confirmed witnessed action is distinct"

    Expect.equal
        (applied.GetProperty("data").GetProperty("grantRevision").GetString())
        "3"
        "Revision is exact text"

    Expect.equal runtime.ManagementCalls 3 "One management call per admitted request"

let tests =
    testList
        "Web typed core routes"
        [
            testCase
                "[CC-WEB-001] delegates case lookup to the endpoint-specific typed core method"
                getDelegation
            testCase
                "[CC-WEB-001] delegates recovery resolution only through IClaimsCore.Recovery"
                recoveryDelegation
            testCase
                "[CC-WEB-001] rejects malformed transport before core invocation"
                refusalBeforeCore
            testCase
                "[CC-WEB-001] refuses raw retention without its exact source digest header"
                rawRetainHeader
            testCase
                "[CC-WEB-001] keeps management refusal non-disclosing and uncertain event identity exact"
                managementOutcomes
        ]
