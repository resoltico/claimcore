namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

module internal ActorGrantCandidate =
    let private fields =
        [|
            "version"
            "eventId"
            "revision"
            "action"
            "targetActorId"
            "approverActorId"
            "principalKind"
            "issuer"
            "principalValue"
            "scopeKind"
            "scopeCaseId"
            "role"
            "enabled"
        |]

    let roleName =
        function
        | Role.Owner -> "OWNER"
        | Role.CaseReader -> "CASE_READER"
        | Role.CaseEditor -> "CASE_EDITOR"
        | Role.RecoveryOperator -> "RECOVERY_OPERATOR"
        | Role.RecoveryExporter -> "RECOVERY_EXPORTER"
        | Role.AuditorCustodian -> "AUDITOR_CUSTODIAN"
        | Role.DataSteward -> "DATA_STEWARD"

    let scope =
        function
        | GrantScope.Installation -> "INSTALLATION", Guid.Empty
        | GrantScope.Case caseId -> "CASE", caseId

    let encode (value: ActorAuthorityAction) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteNumber("version", 1)
        writer.WriteString("eventId", value.EventId)
        writer.WriteNumber("revision", value.Revision)
        writer.WriteString("action", value.ActionName)
        writer.WriteString("targetActorId", value.TargetActorId)

        match value.ApproverActorId with
        | Some actor -> writer.WriteString("approverActorId", actor)
        | None -> writer.WriteNull("approverActorId")

        match value.Principal with
        | Some principal ->
            let kind, issuer, stable = PrincipalKey.storageParts principal
            writer.WriteString("principalKind", kind)
            writer.WriteString("issuer", issuer)
            writer.WriteString("principalValue", stable)
        | None ->
            writer.WriteNull("principalKind")
            writer.WriteNull("issuer")
            writer.WriteNull("principalValue")

        match value.Grant with
        | Some grant ->
            let kind, caseId = scope grant.Scope
            writer.WriteString("scopeKind", kind)
            writer.WriteString("scopeCaseId", caseId)
            writer.WriteString("role", roleName grant.Role)
        | None ->
            writer.WriteNull("scopeKind")
            writer.WriteNull("scopeCaseId")
            writer.WriteNull("role")

        match value.Enabled with
        | Some enabled -> writer.WriteBoolean("enabled", enabled)
        | None -> writer.WriteNull("enabled")

        writer.WriteEndObject()
        writer.Flush()
        let canonical = stream.ToArray()
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        canonical

    let private roleFromText =
        function
        | "OWNER" -> Some Role.Owner
        | "CASE_READER" -> Some Role.CaseReader
        | "CASE_EDITOR" -> Some Role.CaseEditor
        | "RECOVERY_OPERATOR" -> Some Role.RecoveryOperator
        | "RECOVERY_EXPORTER" -> Some Role.RecoveryExporter
        | "AUDITOR_CUSTODIAN" -> Some Role.AuditorCustodian
        | "DATA_STEWARD" -> Some Role.DataSteward
        | _ -> None

    let grantFromStorage scopeKind scopeCaseId roleName =
        match scopeKind, scopeCaseId, roleFromText roleName with
        | "INSTALLATION", caseId, Some role when caseId = Guid.Empty ->
            Some
                {
                    Role = role
                    Scope = GrantScope.Installation
                }
        | "CASE", caseId, Some role when caseId <> Guid.Empty ->
            Some
                {
                    Role = role
                    Scope = GrantScope.Case caseId
                }
        | _ -> None

    let private optionalText (root: JsonElement) (name: string) =
        let property = root.GetProperty(name)

        match property.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> property.GetString() |> Option.ofObj
        | _ -> raise (InvalidDataException("Authority candidate field kind is invalid."))

    let private optionalGuid root (name: string) =
        optionalText root name |> Option.map (fun value -> Guid.ParseExact(value, "D"))

    let private optionalBool (root: JsonElement) (name: string) =
        let property = root.GetProperty(name)

        match property.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.True -> Some true
        | JsonValueKind.False -> Some false
        | _ -> raise (InvalidDataException("Authority candidate Boolean is invalid."))

    let private decodePrincipal (root: JsonElement) =
        match
            optionalText root "principalKind",
            optionalText root "issuer",
            optionalText root "principalValue"
        with
        | None, None, None -> None
        | Some kind, Some issuer, Some stable ->
            PrincipalKey.fromStorage kind issuer stable
            |> Result.toOption
            |> Option.map Some
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("Authority candidate principal is invalid.")))
        | _ -> raise (InvalidDataException("Authority candidate principal is partial."))

    let private decodeGrant (root: JsonElement) =
        match
            optionalText root "scopeKind", optionalGuid root "scopeCaseId", optionalText root "role"
        with
        | None, None, None -> None
        | Some kind, Some caseId, Some role ->
            grantFromStorage kind caseId role
            |> Option.orElseWith (fun () ->
                raise (InvalidDataException("Authority candidate grant is invalid.")))
        | _ -> raise (InvalidDataException("Authority candidate grant is invalid."))

    let private shape (action: ActorAuthorityAction) =
        match action.ActionName with
        | "PROVISION_INITIAL_OWNER" ->
            action.Revision = 1L
            && action.ApproverActorId.IsNone
            && action.Principal.IsSome
            && action.Grant =
                Some
                    {
                        Role = Role.Owner
                        Scope = GrantScope.Installation
                    }
            && action.Enabled = Some true
        | "REGISTER_ACTOR" ->
            action.ApproverActorId.IsSome
            && action.Principal.IsSome
            && action.Grant.IsNone
            && action.Enabled = Some true
        | "DISABLE_ACTOR" ->
            action.ApproverActorId.IsSome
            && action.Principal.IsNone
            && action.Grant.IsNone
            && action.Enabled = Some false
        | "ENABLE_ACTOR" ->
            action.ApproverActorId.IsSome
            && action.Principal.IsNone
            && action.Grant.IsNone
            && action.Enabled = Some true
        | "GRANT_ROLE" ->
            action.ApproverActorId.IsSome
            && action.Principal.IsNone
            && action.Grant.IsSome
            && action.Enabled = Some true
        | "REVOKE_ROLE" ->
            action.ApproverActorId.IsSome
            && action.Principal.IsNone
            && action.Grant.IsSome
            && action.Enabled = Some false
        | _ -> false

    let private readAction (root: JsonElement) : ActorAuthorityAction =
        {
            EventId = root.GetProperty("eventId").GetGuid()
            Revision = root.GetProperty("revision").GetInt64()
            ActionName =
                optionalText root "action"
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("Authority action is absent.")))
            TargetActorId = root.GetProperty("targetActorId").GetGuid()
            ApproverActorId = optionalGuid root "approverActorId"
            Principal = decodePrincipal root
            Grant = decodeGrant root
            Enabled = optionalBool root "enabled"
        }

    let private exactRow
        action
        canonical
        revision
        eventId
        actionName
        targetActorId
        approverActorId
        =
        let rebuilt = encode action

        try
            shape action
            && action.Revision = revision
            && action.EventId = eventId
            && action.ActionName = actionName
            && action.TargetActorId = targetActorId
            && action.ApproverActorId = approverActorId
            && rebuilt = canonical
        finally
            CryptographicOperations.ZeroMemory(rebuilt)

    /// Reconstructs the typed action and its exact canonical bytes. Stored JSON is evidence,
    /// not a permissive transport; alternate ordering, whitespace and escapes are refused.
    let decodeStored
        (canonical: byte array)
        (revision: int64)
        (eventId: Guid)
        (actionName: string)
        (targetActorId: Guid)
        (approverActorId: Guid option)
        =
        try
            if canonical.Length < 2 || canonical.Length > 8192 then
                None
            else
                use document = JsonDocument.Parse(canonical)
                let root = document.RootElement
                let names = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

                if names <> fields || root.GetProperty("version").GetInt32() <> 1 then
                    None
                else
                    let action = readAction root

                    if
                        exactRow
                            action
                            canonical
                            revision
                            eventId
                            actionName
                            targetActorId
                            approverActorId
                    then
                        Some action
                    else
                        None
        with
        | :? JsonException
        | :? InvalidDataException
        | :? InvalidOperationException
        | :? ArgumentException
        | :? OverflowException -> None

    let verifyStored canonical revision eventId actionName targetActorId approverActorId =
        decodeStored canonical revision eventId actionName targetActorId approverActorId
        |> Option.isSome

    let enabledAction eventId revision actorId approverId enabled : ActorAuthorityAction =
        {
            EventId = eventId
            Revision = revision
            ActionName = if enabled then "ENABLE_ACTOR" else "DISABLE_ACTOR"
            TargetActorId = actorId
            ApproverActorId = Some approverId
            Principal = None
            Grant = None
            Enabled = Some enabled
        }
