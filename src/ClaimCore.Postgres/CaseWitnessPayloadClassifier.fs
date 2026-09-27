namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ClaimCore.Witness

/// Every recognized pre-cutoff CASE payload is pruned. Unknown candidate versions or phases
/// refuse the operation; this is not a fallback that silently preserves sensitive ciphertext.
module internal CaseWitnessPayloadClassifier =
    let private registrationPrefix =
        Encoding.ASCII.GetBytes("CLAIMCORE_OWNER_MANAGED_COPY_REGISTER_V1\000")

    let private transitionPrefix =
        Encoding.ASCII.GetBytes("CLAIMCORE_OWNER_MANAGED_COPY_TRANSITION_V1\000")

    let private invalid () = raise WitnessPending

    let private sameCase (ticket: Ticket) (caseId: Guid) =
        ticket.ScopeKind = Case
        && ticket.SubjectCaseId = Some caseId
        && caseId <> Guid.Empty

    let private guid (root: JsonElement) (name: string) = root.GetProperty(name).GetGuid()
    let private text (root: JsonElement) (name: string) = root.GetProperty(name).GetString()

    let private directCase (ticket: Ticket) (root: JsonElement) eventField =
        sameCase ticket (guid root "caseId")
        && guid root eventField = ticket.OperationId

    let private lifecycleEvent (ticket: Ticket) (root: JsonElement) =
        let draft = root.GetProperty("draft").GetBytesFromBase64()

        try
            use document = JsonDocument.Parse(draft)
            let embedded = document.RootElement

            text embedded "kind" = "CASE_LIFECYCLE_DRAFT"
            && embedded.GetProperty("version").GetInt32() = 1
            && directCase ticket embedded "eventId"
        finally
            CryptographicOperations.ZeroMemory(draft)

    let private version (root: JsonElement) expected =
        root.GetProperty("version").GetInt32() = expected

    let private lifecycleKind ticket root =
        function
        | "CASE_LIFECYCLE_DRAFT" -> Some(version root 1 && directCase ticket root "eventId")
        | "CASE_LIFECYCLE_EVENT" -> Some(version root 1 && lifecycleEvent ticket root)
        | "CASE_LIFECYCLE_APPROVAL" -> Some(version root 1 && directCase ticket root "approvalId")
        | "CASE_ERASURE_LIVE_PURGE"
        | "CASE_TOMBSTONE_HOLD_RECORD"
        | "CASE_TOMBSTONE_HOLD_RELEASE"
        | "CASE_WITNESS_PRUNE_EXECUTION" -> Some(version root 1 && directCase ticket root "eventId")
        | "CASE_WITNESS_PRUNE_APPROVAL" ->
            Some(version root 1 && directCase ticket root "approvalId")
        | _ -> None

    let private otherKind ticket root =
        function
        | "PREPARE" ->
            version root 1
            && sameCase ticket (guid root "caseId")
            && ticket.OperationId = WitnessTechnical.prepareEventId (guid root "operationId")
        | "START" -> version root 1 && directCase ticket root "attemptId"
        | "REVOCATION" -> version root 2 && directCase ticket root "operationId"
        | "RECOVERY_EXPORT_V4" -> directCase ticket root "exportId"
        | _ -> false

    let private jsonIntent (ticket: Ticket) (plain: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(plain))
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            invalid ()

        let kindProperty =
            match root.TryGetProperty("kind") with
            | true, property -> Some property
            | false, _ -> None

        let accepted () =
            root.GetProperty("version").GetInt32() = 2
            && directCase ticket root "operationId"
            && root.GetProperty("canonicalRequest").ValueKind = JsonValueKind.String
            && root.GetProperty("canonicalSnapshot").ValueKind = JsonValueKind.String

        let actorGrant () =
            root.GetProperty("version").GetInt32() = 1
            && guid root "eventId" = ticket.OperationId
            && text root "scopeKind" = "CASE"
            && sameCase ticket (guid root "scopeCaseId")
            && root.GetProperty("principalValue").ValueKind <> JsonValueKind.Undefined

        let externalPublication () =
            root.GetProperty("version").GetInt32() = 1
            && text root "action" = "PUBLISH_EXTERNAL_COPY"
            && directCase ticket root "publicationId"
            && root.GetProperty("copyId").ValueKind = JsonValueKind.String

        let isExternalPublication = root.TryGetProperty("action") |> fst

        let recognized =
            match kindProperty with
            | None when isExternalPublication -> externalPublication ()
            | None -> accepted () || actorGrant ()
            | Some property ->
                let kind = property.GetString() |> Option.ofObj |> Option.defaultWith invalid

                match lifecycleKind ticket root kind with
                | Some supported -> supported
                | None -> otherKind ticket root kind

        if not recognized then
            invalid ()

        isExternalPublication

    let private copyIntent (ticket: Ticket) (plain: byte array) =
        let prefix, parse =
            if plain.AsSpan().StartsWith(registrationPrefix) then
                registrationPrefix,
                (fun bytes ->
                    ManagedCopyRegistrationAttestation.parse bytes
                    |> Option.map (fun value ->
                        value.EventId, value.SigningKeyId, value.SourceCaseId))
            elif plain.AsSpan().StartsWith(transitionPrefix) then
                transitionPrefix,
                (fun bytes ->
                    ManagedCopyTransitionAttestation.parse bytes
                    |> Option.map (fun value ->
                        value.Copy.EventId, value.Copy.SigningKeyId, value.Copy.SourceCaseId))
            else
                invalid ()

        let headerLength = prefix.Length + 16 + 16 + 4

        if plain.Length < headerLength + 64 then
            invalid ()

        let eventId = Guid(plain.AsSpan(prefix.Length, 16))
        let signingKey = Guid(plain.AsSpan(prefix.Length + 16, 16))
        let length = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(headerLength - 4, 4))

        if length < 1 || length > 300000 || plain.Length <> headerLength + length + 64 then
            invalid ()

        let canonical = plain.AsSpan(headerLength, length).ToArray()

        try
            match parse canonical with
            | Some(parsedEvent, parsedKey, Some caseId) when
                eventId = ticket.OperationId
                && eventId = parsedEvent
                && signingKey = parsedKey
                && sameCase ticket caseId
                ->
                false
            | _ -> invalid ()
        finally
            CryptographicOperations.ZeroMemory(canonical)

    /// Caller must independently verify the full immutable journal chain and ciphertext AEAD.
    /// A recognized candidate is still pruned; recognition only prevents unknown-kind bypass.
    let classifyIntent (ticket: Ticket) (plain: byte array) =
        if
            ticket.Phase <> Intent
            || ticket.ScopeKind <> Case
            || ticket.SubjectCaseId.IsNone
            || isNull (box plain)
            || plain.Length < 2
            || plain.Length > 1040000
        then
            invalid ()

        try
            if plain[0] = byte '{' then
                jsonIntent ticket plain
            else
                copyIntent ticket plain
        with _ ->
            invalid ()
