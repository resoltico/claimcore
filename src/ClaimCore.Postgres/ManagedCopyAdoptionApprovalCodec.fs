namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

[<NoEquality; NoComparison>]
type internal DecodedCopyAdoptionApproval =
    {
        Request: CopyAdoptionApprovalRequest
        ActorId: Guid
        GrantRevision: int64
        ApprovedAt: DateTimeOffset
    }

/// Decodes only the closed canonical approval grammar; a re-encode check rejects alternate
/// spellings, extra properties and noncanonical timestamps before audit uses any field.
module internal ManagedCopyAdoptionApprovalCodec =
    let private names =
        [|
            "version"
            "action"
            "approvalId"
            "adoptionEventId"
            "copyId"
            "caseId"
            "originKind"
            "exportId"
            "preFenceSequence"
            "preFenceHash"
            "ciphertextSha256"
            "ciphertextBytes"
            "capturedAt"
            "retainUntil"
            "locationCommitment"
            "custodianCommitment"
            "custodianSigningKeyId"
            "registrySigningKeyId"
            "inspectorSigningKeyId"
            "custodianCanonicalSha256"
            "registryCanonicalSha256"
            "inspectionReportSha256"
            "approvedAt"
            "expiresAt"
            "ownerActorId"
            "ownerGrantRevision"
        |]

    let private property (root: JsonElement) (name: string) = root.GetProperty(name)

    let private text root name =
        (property root name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Copy adoption approval has null text.")

    let private guid root name = (property root name).GetGuid()
    let private number root name = (property root name).GetInt64()
    let private bytes root name = text root name |> Convert.FromHexString

    let private instant root name =
        DateTimeOffset.ParseExact(
            text root name,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None
        )

    let private origin (root: JsonElement) =
        let sequence = number root "preFenceSequence"
        let hash = bytes root "preFenceHash"

        match text root "originKind" with
        | "PRODUCT_EXPORT" -> CopyAdoptionOrigin.ProductExport(guid root "exportId", sequence, hash)
        | "ADOPTED_EXTERNAL" when (property root "exportId").ValueKind = JsonValueKind.Null ->
            CopyAdoptionOrigin.AdoptedExternal(sequence, hash)
        | _ -> invalidOp "Copy adoption origin is invalid."

    let private request (root: JsonElement) =
        {
            ApprovalId = guid root "approvalId"
            AdoptionEventId = guid root "adoptionEventId"
            CopyId = guid root "copyId"
            CaseId = guid root "caseId"
            Origin = origin root
            CiphertextSha256 = bytes root "ciphertextSha256"
            CiphertextBytes = number root "ciphertextBytes"
            CapturedAt = instant root "capturedAt"
            RetainUntil = instant root "retainUntil"
            LocationCommitment = bytes root "locationCommitment"
            CustodianCommitment = bytes root "custodianCommitment"
            CustodianSigningKeyId = guid root "custodianSigningKeyId"
            RegistrySigningKeyId = guid root "registrySigningKeyId"
            InspectorSigningKeyId = guid root "inspectorSigningKeyId"
            CustodianCanonicalSha256 = bytes root "custodianCanonicalSha256"
            RegistryCanonicalSha256 = bytes root "registryCanonicalSha256"
            InspectionReportSha256 = bytes root "inspectionReportSha256"
            ExpiresAt = instant root "expiresAt"
        }

    let decode (canonical: byte array) =
        if isNull (box canonical) || canonical.Length < 2 || canonical.Length > 8192 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement
                let actual = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

                if
                    actual <> names
                    || (property root "version").GetInt32() <> 1
                    || text root "action" <> "APPROVE_COPY_ADOPTION"
                then
                    None
                else
                    let value =
                        {
                            Request = request root
                            ActorId = guid root "ownerActorId"
                            GrantRevision = number root "ownerGrantRevision"
                            ApprovedAt = instant root "approvedAt"
                        }

                    let rebuilt =
                        ManagedCopyAdoptionApprovalCandidate.canonical
                            value.Request
                            value.ActorId
                            value.GrantRevision
                            value.ApprovedAt

                    try
                        if rebuilt = canonical then Some value else None
                    finally
                        CryptographicOperations.ZeroMemory(rebuilt)
            with _ ->
                None
