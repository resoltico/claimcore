module ClaimCore.WebTests.TestServerLifecycleInputs

open Expecto
open ClaimCore.WebTests.TestServerFixture

let private operation = "40000000-0000-4000-8000-000000000001"

let input identifier =
    let change =
        $"""{{"eventId":"{operation}","caseReference":"WEB-V2-001","expectedRevision":"1","expectedLifecycleSequence":"0","expectedLifecycleHash":"{digest}","action":{{"kind":"VOID_DATA_ENTRY_ERROR","reason":"Synthetic entry error"}}}}"""

    match identifier with
    | "lifecycle.review" -> """{"caseReference":"WEB-V2-001"}"""
    | "lifecycle.apply" -> change
    | "lifecycle.approve" ->
        change.Remove(change.Length - 1)
        + $""", "approvalId":"{operation}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""
    | _ -> failtest "Unknown lifecycle endpoint."
