module ClaimCore.WebTests.LifecycleInputTests

open ClaimCore.Contracts

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes(value)
let private eventId = "40000000-0000-4000-8000-000000000001"
let private approvalId = "50000000-0000-4000-8000-000000000001"
let private hash = String.replicate 64 "0"

let private change =
    $"""{{"eventId":"{eventId}","caseReference":"CASE-1","expectedRevision":"1","expectedLifecycleSequence":"0","expectedLifecycleHash":"{hash}","action":{{"kind":"VOID_DATA_ENTRY_ERROR","reason":"Data entry error"}}}}"""

let private pendingDraft () =
    let pending = change.Replace("VOID_DATA_ENTRY_ERROR", "MARK_ERASURE_PENDING")

    match HttpLifecycleInput.apply (bytes pending) with
    | Ok value ->
        match value.Action with
        | LifecycleMutation.MarkErasurePending reason ->
            Expect.equal reason "Data entry error" "Pending transition reason is exact"
        | _ -> failtest "Wrong pending mutation decoded"
    | Error _ -> failtest "Exact pending mutation must decode"

let private purgeApproval () =
    let purge =
        change
            .Replace("VOID_DATA_ENTRY_ERROR", "PURGE_PAYLOAD")
            .Replace(
                "\"reason\":\"Data entry error\"",
                "\"reason\":\"Data entry error\",\"validUntil\":\"2026-10-01T00:00:00.0000000+00:00\""
            )

    match HttpLifecycleInput.apply (bytes purge) with
    | Ok _ -> failtest "Owner-only purge must not be available through lifecycle.apply"
    | Error _ -> ()

    let approved =
        purge.Remove(purge.Length - 1)
        + $""", "approvalId":"{approvalId}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

    match HttpLifecycleInput.approve (bytes approved) with
    | Ok value ->
        match value.Change.Action with
        | LifecycleMutation.PurgeLivePayload(reason, validUntil) ->
            Expect.equal reason "Data entry error" "Approval binds the exact purge reason"

            Expect.equal
                validUntil
                (DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))
                "Exact purge expiry"
        | _ -> failtest "Wrong purge approval mutation decoded"
    | Error _ -> failtest "Exact owner-purge approval must decode"

    let unaligned =
        approved.Replace(
            "\"validUntil\":\"2026-10-01T00:00:00.0000000+00:00\"",
            "\"validUntil\":\"2026-10-01T00:00:00.0000001+00:00\""
        )

    match HttpLifecycleInput.approve (bytes unaligned) with
    | Ok _ -> failtest "Sub-microsecond purge authority cannot be stored exactly."
    | Error _ -> ()

let private exactDraft () =
    match HttpLifecycleInput.apply (bytes change) with
    | Ok value ->
        Expect.equal value.EventId (Guid.Parse eventId) "Caller event ID is retained"
        Expect.equal value.ExpectedRevision 1L "Business revision is exact"
        Expect.equal value.ExpectedLifecycleSequence 0L "Lifecycle sequence is exact"

        match value.Action with
        | LifecycleMutation.VoidDataEntryError reason ->
            Expect.equal reason "Data entry error" "Reason is not normalized"
        | _ -> failtest "Wrong lifecycle mutation decoded"
    | Error _ -> failtest "Exact lifecycle draft must decode"

    pendingDraft ()
    purgeApproval ()

    let approved =
        change.Remove(change.Length - 1)
        + $""", "approvalId":"{approvalId}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

    match HttpLifecycleInput.approve (bytes approved) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approvalId) "Approval identity is exact"
        Expect.equal value.Change.EventId (Guid.Parse eventId) "Approved draft is unchanged"
    | Error _ -> failtest "Exact lifecycle approval must decode"

let private malformed () =
    let reject source =
        match HttpLifecycleInput.apply (bytes source) with
        | Ok _ -> failtest "Malformed lifecycle input was admitted"
        | Error _ -> ()

    reject (change.Replace("\"reason\":", "\"actorId\":\"private\",\"reason\":"))
    reject (change.Replace("VOID_DATA_ENTRY_ERROR", "INVENTED_ACTION"))

    reject (
        change
            .Replace("VOID_DATA_ENTRY_ERROR", "MARK_ERASURE_PENDING")
            .Replace(
                "\"reason\":",
                "\"holdId\":\"50000000-0000-4000-8000-000000000001\",\"reason\":"
            )
    )

    reject (change.Replace(hash, String.replicate 64 "A"))
    reject (change.Replace("\"expectedRevision\":\"1\"", "\"expectedRevision\":\"01\""))
    reject (change.Replace("Data entry error", "\\uD800"))
    reject (change.Replace("\"eventId\":", "\"eventId\":\"{eventId}\",\"eventId\":"))

let tests =
    testList
        "lifecycle input"
        [
            testCase
                "[CC-WEB-001] lifecycle drafts and approvals retain exact caller identities"
                exactDraft
            testCase "[CC-WEB-001] lifecycle input refuses forged or malformed fields" malformed
        ]
