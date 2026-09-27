namespace ClaimCore.Database

open System
open System.Text.Json

[<NoEquality; NoComparison>]
type internal SignedIndependentObservation =
    {
        Role: string
        Body: byte array
        Signature: byte array
        Digest: string
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

/// Rechecks exact raw child bytes; an aggregate summary cannot substitute for a child signature.
module internal DatabaseIndependentHostRaw =
    let private fields digestName =
        [
            "role"
            "canonicalBase64"
            "signatureBase64"
            digestName
            "machineHash"
            "storageHash"
            "adminActorId"
            "hostKeyId"
            "checkedAt"
            "validUntil"
        ]

    let private base64 name (entry: JsonElement) =
        let encoded = DatabaseIndependentHostJson.text name entry
        let bytes = Convert.FromBase64String(encoded)

        if Convert.ToBase64String(bytes) <> encoded then
            invalidOp "Independent observation encoding is noncanonical."

        bytes

    let verify (entry: JsonElement) digestName (publicKeyPem: byte array) expectedRole =
        DatabaseIndependentHostJson.exact (fields digestName) entry
        let body = base64 "canonicalBase64" entry
        let signature = base64 "signatureBase64" entry
        let key = DatabaseIndependentHostJson.rawPublicKey publicKeyPem
        use document = DatabaseIndependentHostJson.signed 16384 key body signature
        let report = document.RootElement
        let digest = DatabaseIndependentHostJson.sha256 body

        if
            body.Length = 0
            || DatabaseIndependentHostJson.text "role" entry <> expectedRole
            || DatabaseIndependentHostJson.text "role" report <> expectedRole
            || DatabaseIndependentHostJson.digest digestName entry <> digest
            || DatabaseIndependentHostJson.digest "machineHash" entry
               <> DatabaseIndependentHostJson.digest "machineHash" report
            || DatabaseIndependentHostJson.digest "storageHash" entry
               <> DatabaseIndependentHostJson.digest "storageHash" report
            || DatabaseIndependentHostJson.uuid "adminActorId" entry
               <> DatabaseIndependentHostJson.uuid "adminActorId" report
            || DatabaseIndependentHostJson.uuid "hostKeyId" entry
               <> DatabaseIndependentHostJson.uuid "hostKeyId" report
        then
            invalidOp "Independent observation summary diverged."

        {
            Role = expectedRole
            Body = body
            Signature = signature
            Digest = digest
            CheckedAt = DatabaseIndependentHostJson.instant "checkedAt" entry
            ValidUntil = DatabaseIndependentHostJson.instant "validUntil" entry
        }

    let withReport (observation: SignedIndependentObservation) action =
        use document = DatabaseIndependentHostJson.canonical 16384 observation.Body
        action document.RootElement
