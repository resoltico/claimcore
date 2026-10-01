namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application
open HttpInputSupport

type HttpLifecycleApprovalInput =
    {
        Change: LifecycleChange
        ApprovalId: Guid
        ExpiresAt: DateTimeOffset
    }

module HttpLifecycleInput =
    let private dateOnly (element: JsonElement) =
        let source = stringValue element
        let mutable value = DateOnly.MinValue

        if
            not (
                DateOnly.TryParseExact(
                    source,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    &value
                )
            )
        then
            fail HttpInputProblem.InvalidJson

        value

    let private timestamp (element: JsonElement) =
        stringValue element |> utcTimestampValue

    let private action allowPurge (element: JsonElement) =
        let values = properties element
        let kind = required "kind" values |> stringValue

        match kind with
        | "VOID_DATA_ENTRY_ERROR" ->
            exactProperties [ "kind"; "reason" ] values |> ignore
            LifecycleMutation.VoidDataEntryError(required "reason" values |> stringValue)
        | "REINSTATE_VOIDED" ->
            exactProperties [ "kind"; "reason" ] values |> ignore
            LifecycleMutation.ReinstateVoided(required "reason" values |> stringValue)
        | "REQUEST_ERASURE" ->
            exactProperties [ "kind"; "reason" ] values |> ignore
            LifecycleMutation.RequestErasure(required "reason" values |> stringValue)
        | "MARK_ERASURE_PENDING" ->
            exactProperties [ "kind"; "reason" ] values |> ignore
            LifecycleMutation.MarkErasurePending(required "reason" values |> stringValue)
        | "PURGE_PAYLOAD" when allowPurge ->
            exactProperties [ "kind"; "reason"; "validUntil" ] values |> ignore

            LifecycleMutation.PurgeLivePayload(
                required "reason" values |> stringValue,
                required "validUntil" values |> stringValue |> utcMicrosecondTimestampValue
            )
        | "RECORD_HOLD" ->
            exactProperties [ "kind"; "holdId"; "ground"; "reviewOn" ] values |> ignore

            LifecycleMutation.RecordHold(
                required "holdId" values |> stringValue |> operationIdValue,
                required "ground" values |> stringValue,
                required "reviewOn" values |> dateOnly
            )
        | "RELEASE_HOLD" ->
            exactProperties [ "kind"; "holdId"; "reason" ] values |> ignore

            LifecycleMutation.ReleaseHold(
                required "holdId" values |> stringValue |> operationIdValue,
                required "reason" values |> stringValue
            )
        | _ -> fail HttpInputProblem.InvalidJson

    let private change allowPurge values =
        {
            EventId = required "eventId" values |> stringValue |> operationIdValue
            CaseReference = required "caseReference" values |> stringValue
            ExpectedRevision =
                required "expectedRevision" values |> stringValue |> canonicalNonNegativeInt64
            ExpectedLifecycleSequence =
                required "expectedLifecycleSequence" values
                |> stringValue
                |> canonicalNonNegativeInt64
            ExpectedLifecycleHash =
                required "expectedLifecycleHash" values |> stringValue |> digestValue
            Action = required "action" values |> action allowPurge
        }

    let review bytes =
        parse bytes (fun root ->
            let values = properties root |> exactProperties [ "caseReference" ]
            required "caseReference" values |> stringValue)

    let apply bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "eventId"
                        "caseReference"
                        "expectedRevision"
                        "expectedLifecycleSequence"
                        "expectedLifecycleHash"
                        "action"
                    ]

            change false values)

    let approve bytes =
        parse bytes (fun root ->
            let values =
                properties root
                |> exactProperties
                    [
                        "eventId"
                        "caseReference"
                        "expectedRevision"
                        "expectedLifecycleSequence"
                        "expectedLifecycleHash"
                        "action"
                        "approvalId"
                        "expiresAt"
                    ]

            {
                Change = change true values
                ApprovalId = required "approvalId" values |> stringValue |> operationIdValue
                ExpiresAt = required "expiresAt" values |> timestamp
            })
