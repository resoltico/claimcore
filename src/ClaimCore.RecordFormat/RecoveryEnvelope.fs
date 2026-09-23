namespace ClaimCore.RecordFormat

open System
open System.Security.Cryptography

/// Exact, installation-bound transfer format for one canonical recovery request.
[<NoEquality; NoComparison>]
type RecoveryEnvelope =
    {
        InstallationId: Guid
        OperationId: Guid
        CanonicalCommandFormat: int
        RequestFingerprintVersion: int
        RequestSha256: string
        CanonicalRequest: byte array
    }

module RecoveryEnvelope =
    let private format = "claimcore-recovery"
    let private formatVersion = 2

    let private canonicalId path raw =
        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> Json.reject path "Use one non-empty UUID in canonical lowercase hyphenated form."

    let private digest (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexStringLower

    let private validDigest (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let private exactShape root =
        Json.properties
            "$"
            [
                "format"
                "formatVersion"
                "installationId"
                "operationId"
                "canonicalCommandFormat"
                "requestFingerprintVersion"
                "requestSha256"
                "canonicalRequestBase64"
            ]
            root

        if
            Json.text "$" "format" root <> format
            || Json.integer "$" "formatVersion" root <> int64 formatVersion
        then
            Json.reject "$" "The recovery envelope format is unsupported."

    let private versions root =
        let canonicalCommandFormat = Json.integer "$" "canonicalCommandFormat" root
        let fingerprintVersion = Json.integer "$" "requestFingerprintVersion" root

        if
            canonicalCommandFormat <> int64 RecordVersions.CanonicalCommandFormat
            || fingerprintVersion <> int64 RecordVersions.RequestFingerprint
        then
            Json.reject "$" "The recovery envelope version is unsupported."

        int canonicalCommandFormat, int fingerprintVersion

    let private canonicalRequest maximumCanonicalRequestBytes root =
        let requestSha256 = Json.text "$" "requestSha256" root

        if not (validDigest requestSha256) then
            Json.reject "$.requestSha256" "Use one lowercase SHA-256 digest."

        let encoded = Json.text "$" "canonicalRequestBase64" root

        let canonicalRequest =
            try
                Convert.FromBase64String(encoded)
            with :? FormatException ->
                Json.reject "$.canonicalRequestBase64" "Use canonical Base64 request bytes."

        if
            canonicalRequest.Length = 0
            || canonicalRequest.Length > maximumCanonicalRequestBytes
            || Convert.ToBase64String(canonicalRequest) <> encoded
            || digest canonicalRequest <> requestSha256
        then
            Json.reject "$" "The recovery envelope failed integrity checks."

        requestSha256, canonicalRequest

    let private verifyRequest maximumCanonicalRequestBytes operationId canonicalRequest =
        match RequestRecord.decode maximumCanonicalRequestBytes canonicalRequest with
        | Error _ -> Json.reject "$" "The recovery envelope request is invalid."
        | Ok request when
            request.OperationId <> operationId
            || not (
                CryptographicOperations.FixedTimeEquals(
                    RequestRecord.encode request,
                    canonicalRequest
                )
            )
            ->
            Json.reject "$" "The recovery envelope request is not canonical."
        | Ok _ -> ()

    let private read maximumCanonicalRequestBytes root =
        exactShape root
        let canonicalCommandFormat, fingerprintVersion = versions root
        let requestSha256, canonical = canonicalRequest maximumCanonicalRequestBytes root

        let operationId = Json.text "$" "operationId" root |> canonicalId "$.operationId"
        verifyRequest maximumCanonicalRequestBytes operationId canonical

        {
            InstallationId = Json.text "$" "installationId" root |> canonicalId "$.installationId"
            OperationId = operationId
            CanonicalCommandFormat = canonicalCommandFormat
            RequestFingerprintVersion = fingerprintVersion
            RequestSha256 = requestSha256
            CanonicalRequest = canonical
        }

    let decode maximumCanonicalRequestBytes bytes =
        if maximumCanonicalRequestBytes < 1 then
            invalidArg
                (nameof maximumCanonicalRequestBytes)
                "The canonical request limit must be positive."

        Json.parse bytes (read maximumCanonicalRequestBytes)
        |> Result.mapError _.Message

    let encode (envelope: RecoveryEnvelope) =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("format", format)
            writer.WriteNumber("formatVersion", formatVersion)
            writer.WriteString("installationId", envelope.InstallationId)
            writer.WriteString("operationId", envelope.OperationId)
            writer.WriteNumber("canonicalCommandFormat", envelope.CanonicalCommandFormat)
            writer.WriteNumber("requestFingerprintVersion", envelope.RequestFingerprintVersion)
            writer.WriteString("requestSha256", envelope.RequestSha256)

            writer.WriteString(
                "canonicalRequestBase64",
                Convert.ToBase64String(envelope.CanonicalRequest)
            )

            writer.WriteEndObject())
