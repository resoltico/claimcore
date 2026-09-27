namespace ClaimCore.HostSecurity

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Owner-private installation key material; callers receive only domain-separated commitments.
[<Sealed>]
type SuppressionKeyFile private (keyId: Guid, material: byte array) =
    let mutable disposed = false

    let compute installationId lineageId label (value: byte array) =
        if disposed then
            invalidOp "Suppression key custody is unavailable."

        if installationId = Guid.Empty || lineageId = Guid.Empty then
            invalidArg (nameof installationId) "Invalid installation identity."

        let prefix =
            Encoding.UTF8.GetBytes(
                "claimcore:suppression:v1:"
                + installationId.ToString("D")
                + ":"
                + lineageId.ToString("D")
                + ":"
                + keyId.ToString("D")
                + ":"
                + label
                + ":"
            )

        let input = Array.concat [ prefix; value ]

        try
            HMACSHA256.HashData(material, input)
        finally
            CryptographicOperations.ZeroMemory(input)

    member _.KeyId = keyId

    member _.Check(installationId, lineageId) =
        compute installationId lineageId "key-check" Array.empty

    member _.Reference(installationId, lineageId, caseReference: string) =
        if String.IsNullOrEmpty(caseReference) then
            invalidArg (nameof caseReference) "Invalid reference."

        compute installationId lineageId "reference" (Encoding.UTF8.GetBytes(caseReference))

    member _.Operation(installationId, lineageId, operationId: Guid) =
        if operationId = Guid.Empty then
            invalidArg (nameof operationId) "Invalid operation."

        compute
            installationId
            lineageId
            "operation"
            (Encoding.UTF8.GetBytes(operationId.ToString("D")))

    member _.RequestCandidate(installationId, lineageId, canonical: byte array) =
        if isNull (box canonical) || canonical.Length < 1 || canonical.Length > 16384 then
            invalidArg (nameof canonical) "Erasure candidate size is invalid."

        compute installationId lineageId "request-candidate" canonical

    member _.PurgeProposal(installationId, lineageId, canonicalDraft: byte array) =
        if
            isNull (box canonicalDraft)
            || canonicalDraft.Length < 1
            || canonicalDraft.Length > 16384
        then
            invalidArg (nameof canonicalDraft) "Erasure proposal size is invalid."

        compute installationId lineageId "purge-proposal" canonicalDraft

    member _.ApprovalDraft(installationId, lineageId, canonicalDraft: byte array) =
        if
            isNull (box canonicalDraft)
            || canonicalDraft.Length < 1
            || canonicalDraft.Length > 16384
        then
            invalidArg (nameof canonicalDraft) "Erasure approval draft size is invalid."

        compute installationId lineageId "approval-draft" canonicalDraft

    member _.ApprovalCanonical(installationId, lineageId, canonicalApproval: byte array) =
        if
            isNull (box canonicalApproval)
            || canonicalApproval.Length < 1
            || canonicalApproval.Length > 8192
        then
            invalidArg (nameof canonicalApproval) "Erasure approval size is invalid."

        compute installationId lineageId "approval-canonical" canonicalApproval

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(material)

    static member Load(path: string) =
        match PrivateFileService.readBinary 8192 path with
        | Error _ -> invalidOp "Private suppression key is unavailable."
        | Ok bytes ->
            try
                try
                    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                    let root = document.RootElement

                    if root.ValueKind <> JsonValueKind.Object then
                        invalidOp "Private suppression key is invalid."

                    let names = root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

                    if
                        names <> set [ "version"; "keyId"; "materialBase64" ]
                        || root.GetProperty("version").GetInt32() <> 1
                    then
                        invalidOp "Private suppression key is invalid."

                    let keyId = root.GetProperty("keyId").GetGuid()

                    let encoded =
                        root.GetProperty("materialBase64").GetString()
                        |> Option.ofObj
                        |> Option.defaultWith (fun () ->
                            invalidOp "Private suppression key is invalid.")

                    if keyId = Guid.Empty then
                        invalidOp "Private suppression key is invalid."

                    let material = Convert.FromBase64String(encoded)

                    if material.Length <> 32 || Convert.ToBase64String(material) <> encoded then
                        CryptographicOperations.ZeroMemory(material)
                        invalidOp "Private suppression key is invalid."

                    new SuppressionKeyFile(keyId, material)
                with _ ->
                    invalidOp "Private suppression key is invalid."
            finally
                CryptographicOperations.ZeroMemory(bytes)
