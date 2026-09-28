namespace ClaimCore.Postgres

open System
open System.IO
open System.Text.Json

[<NoEquality; NoComparison>]
type internal AdoptedCopyTransition =
    {
        CopyId: Guid
        SourceCaseId: Guid
        AdoptionEventId: Guid
        ProducerKind: string
        OriginEventHash: byte array
        EventId: Guid
        Revision: int64
        EventKind: string
        State: string
        PreviousEventHash: byte array
        ActionWitnessCutoffSequence: int64
        ActionWitnessCutoffHash: byte array
        VerificationProofSha256: byte array option
        LastVerifiedAt: DateTimeOffset option
        DeletionProofSha256: byte array option
        DeletionApprovalId: Guid option
    }

/// A closed signed transition that binds the verified adoption receipt, never an owner REGISTER.
module internal ManagedCopyAdoptedTransitionAttestation =
    let private names =
        [
            "actionWitnessCutoffHash"
            "actionWitnessCutoffSequence"
            "adoptionEventId"
            "copyId"
            "deletionApprovalId"
            "deletionProofSha256"
            "eventId"
            "eventKind"
            "format"
            "lastVerifiedAt"
            "originEventHash"
            "previousEventHash"
            "producerKind"
            "revision"
            "sourceCaseId"
            "state"
            "verificationProofSha256"
        ]

    let private hex (value: byte array) = Convert.ToHexStringLower value

    let private optionalString (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some text -> writer.WriteString(name, text)
        | None -> writer.WriteNull(name)

    let encode (value: AdoptedCopyTransition) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("actionWitnessCutoffHash", hex value.ActionWitnessCutoffHash)
        writer.WriteNumber("actionWitnessCutoffSequence", value.ActionWitnessCutoffSequence)
        writer.WriteString("adoptionEventId", value.AdoptionEventId)
        writer.WriteString("copyId", value.CopyId)
        optionalString writer "deletionApprovalId" (value.DeletionApprovalId |> Option.map string)
        optionalString writer "deletionProofSha256" (value.DeletionProofSha256 |> Option.map hex)
        writer.WriteString("eventId", value.EventId)
        writer.WriteString("eventKind", value.EventKind)
        writer.WriteString("format", "claimcore-adopted-copy-transition-1")

        optionalString
            writer
            "lastVerifiedAt"
            (value.LastVerifiedAt |> Option.map (fun time -> time.ToString("O")))

        writer.WriteString("originEventHash", hex value.OriginEventHash)
        writer.WriteString("previousEventHash", hex value.PreviousEventHash)
        writer.WriteString("producerKind", value.ProducerKind)
        writer.WriteNumber("revision", value.Revision)
        writer.WriteString("sourceCaseId", value.SourceCaseId)
        writer.WriteString("state", value.State)

        optionalString
            writer
            "verificationProofSha256"
            (value.VerificationProofSha256 |> Option.map hex)

        writer.WriteEndObject()
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]

    let private optional parser (root: JsonElement) (name: string) =
        match root.GetProperty(name).ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(parser root name)
        | _ -> invalidOp "Adopted-copy transition optional field is malformed."

    let private decode (root: JsonElement) : AdoptedCopyTransition =
        {
            CopyId = ManagedCopyAdoptionDocumentCommon.uuid root "copyId"
            SourceCaseId = ManagedCopyAdoptionDocumentCommon.uuid root "sourceCaseId"
            AdoptionEventId = ManagedCopyAdoptionDocumentCommon.uuid root "adoptionEventId"
            ProducerKind = ManagedCopyAdoptionDocumentCommon.text root "producerKind"
            OriginEventHash = ManagedCopyAdoptionDocumentCommon.digest root "originEventHash"
            EventId = ManagedCopyAdoptionDocumentCommon.uuid root "eventId"
            Revision = ManagedCopyAdoptionDocumentCommon.number root "revision"
            EventKind = ManagedCopyAdoptionDocumentCommon.text root "eventKind"
            State = ManagedCopyAdoptionDocumentCommon.text root "state"
            PreviousEventHash = ManagedCopyAdoptionDocumentCommon.digest root "previousEventHash"
            ActionWitnessCutoffSequence =
                ManagedCopyAdoptionDocumentCommon.number root "actionWitnessCutoffSequence"
            ActionWitnessCutoffHash =
                ManagedCopyAdoptionDocumentCommon.digest root "actionWitnessCutoffHash"
            VerificationProofSha256 =
                optional ManagedCopyAdoptionDocumentCommon.digest root "verificationProofSha256"
            LastVerifiedAt =
                optional ManagedCopyAdoptionDocumentCommon.instant root "lastVerifiedAt"
            DeletionProofSha256 =
                optional ManagedCopyAdoptionDocumentCommon.digest root "deletionProofSha256"
            DeletionApprovalId =
                optional ManagedCopyAdoptionDocumentCommon.uuid root "deletionApprovalId"
        }

    let private valid (value: AdoptedCopyTransition) =
        value.Revision >= 2L
        && value.ActionWitnessCutoffSequence > 0L
        && (value.ProducerKind = "PRODUCT_EXPORT" || value.ProducerKind = "ADOPTED_EXTERNAL")
        && (match value.EventKind, value.State with
            | "DELETE_REQUEST", "DELETE_PENDING" ->
                value.DeletionApprovalId.IsNone && value.DeletionProofSha256.IsNone
            | "UNKNOWN", "UNKNOWN" ->
                value.DeletionApprovalId.IsNone && value.DeletionProofSha256.IsNone
            | "VERIFIED_DELETED", "VERIFIED_DELETED" ->
                value.DeletionApprovalId.IsSome
                && value.DeletionProofSha256.IsSome
                && value.LastVerifiedAt.IsSome
            | _ -> false)

    let parse canonical =
        ManagedCopyAdoptionDocumentCommon.parse
            names
            "claimcore-adopted-copy-transition-1"
            canonical
            decode
        |> Option.filter (fun value -> valid value && encode value = canonical)
