namespace ClaimCore.Postgres

open System
open System.Globalization
open System.IO
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

type internal LifecycleAuditDecodedEvent =
    {
        CaseId: Guid
        Change: LifecycleChange
        BusinessRevision: int64
        Sequence: int64
        PreviousHash: byte array
        Disposition: CaseDisposition
        Privacy: PrivacyPhase
        Instant: DateTimeOffset
        ActorId: Guid
        GrantRevision: int64
        ApprovalIds: Guid list
        Snapshot: byte array option
        Draft: byte array
    }

module internal CaseLifecycleAuditCodec =
    let private invalid () =
        raise (InvalidDataException("Canonical lifecycle evidence is invalid."))

    let private guard action =
        try
            action ()
        with
        | :? InvalidDataException -> reraise ()
        | _ -> invalid ()

    let private prop (root: JsonElement) (name: string) = root.GetProperty(name)

    let private text root name =
        (prop root name).GetString() |> Option.ofObj |> Option.defaultWith invalid

    let private guid root name = Guid.Parse(text root name)
    let private number root name = (prop root name).GetInt64()
    let private bytes root name = (prop root name).GetBytesFromBase64()

    let private instant root name =
        DateTimeOffset.ParseExact(text root name, "O", CultureInfo.InvariantCulture)

    let private date root name =
        DateOnly.ParseExact(text root name, "yyyy-MM-dd", CultureInfo.InvariantCulture)

    let private mutation (root: JsonElement) =
        match text root "name" with
        | "VOID_DATA_ENTRY_ERROR" -> LifecycleMutation.VoidDataEntryError(text root "reason")
        | "REINSTATE_VOIDED" -> LifecycleMutation.ReinstateVoided(text root "reason")
        | "REQUEST_ERASURE" -> LifecycleMutation.RequestErasure(text root "reason")
        | "MARK_ERASURE_PENDING" -> LifecycleMutation.MarkErasurePending(text root "reason")
        | "PURGE_PAYLOAD" ->
            LifecycleMutation.PurgeLivePayload(text root "reason", instant root "validUntil")
        | "RECORD_HOLD" ->
            LifecycleMutation.RecordHold(
                guid root "holdId",
                text root "ground",
                date root "reviewOn"
            )
        | "RELEASE_HOLD" -> LifecycleMutation.ReleaseHold(guid root "holdId", text root "reason")
        | _ -> invalid ()

    let decodeDraft (raw: byte array) =
        guard (fun () ->
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
            let root = document.RootElement

            if number root "version" <> 1L || text root "kind" <> "CASE_LIFECYCLE_DRAFT" then
                invalid ()

            let caseId = guid root "caseId"

            let change =
                {
                    EventId = guid root "eventId"
                    CaseReference = text root "caseReference"
                    ExpectedRevision = number root "expectedRevision"
                    ExpectedLifecycleSequence = number root "expectedLifecycleSequence"
                    ExpectedLifecycleHash = text root "expectedLifecycleHash"
                    Action = mutation (prop root "action")
                }

            if CaseLifecycleCandidate.draft caseId change <> raw then
                invalid ()

            caseId, change)

    let private decodedEvent (root: JsonElement) =
        let draft = bytes root "draft"
        let caseId, change = decodeDraft draft

        let approvals =
            (prop root "approvalIds").EnumerateArray()
            |> Seq.map (fun value ->
                value.GetString() |> Option.ofObj |> Option.defaultWith invalid |> Guid.Parse)
            |> Seq.toList

        let snapshot =
            let value = prop root "snapshot"

            if value.ValueKind = JsonValueKind.Null then
                None
            else
                Some(value.GetBytesFromBase64())

        {
            CaseId = caseId
            Change = change
            BusinessRevision = number root "businessRevision"
            Sequence = number root "lifecycleSequence"
            PreviousHash = bytes root "previousHash"
            Disposition =
                CaseLifecycleCandidate.parseDisposition (text root "disposition")
                |> Option.defaultWith invalid
            Privacy =
                CaseLifecycleCandidate.parsePrivacy (text root "privacyPhase")
                |> Option.defaultWith invalid
            Instant = instant root "observedUtcInstant"
            ActorId = guid root "actorId"
            GrantRevision = number root "grantRevision"
            ApprovalIds = approvals
            Snapshot = snapshot
            Draft = draft
        }

    let decodeEvent (raw: byte array) =
        guard (fun () ->
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(raw))
            let root = document.RootElement

            if number root "version" <> 1L || text root "kind" <> "CASE_LIFECYCLE_EVENT" then
                invalid ()

            let decoded = decodedEvent root

            let encoded =
                CaseLifecycleCandidate.event
                    decoded.Draft
                    decoded.BusinessRevision
                    decoded.Sequence
                    decoded.PreviousHash
                    decoded.Disposition
                    decoded.Privacy
                    decoded.Instant
                    decoded.ActorId
                    decoded.GrantRevision
                    decoded.ApprovalIds
                    decoded.Snapshot

            if encoded <> raw then
                invalid ()

            decoded)
