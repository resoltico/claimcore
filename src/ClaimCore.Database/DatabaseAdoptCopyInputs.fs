namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// One owner-private proposal is read through one secured file handle; no nested paths are trusted.
module internal DatabaseAdoptCopyInputs =
    let private exactNames (expected: string list) (root: JsonElement) =
        root.ValueKind = JsonValueKind.Object
        && (root.EnumerateObject() |> Seq.map (fun item -> item.Name) |> Seq.toList)
           |> fun names -> names.Length = expected.Length && Set.ofList names = Set.ofList expected

    let private text (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            invalidOp "Private adoption input is invalid."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Private adoption input is invalid.")

    let private guid (root: JsonElement) (name: string) =
        let value = text root name

        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> parsed
        | _ -> invalidOp "Private adoption input is invalid."

    let private binary maximum (root: JsonElement) (name: string) =
        let value = text root name
        let bytes = Convert.FromBase64String(value)

        if
            bytes.Length = 0
            || bytes.Length > maximum
            || Convert.ToBase64String(bytes) <> value
        then
            CryptographicOperations.ZeroMemory(bytes)
            invalidOp "Private adoption input is invalid."

        bytes

    let private document (root: JsonElement) prefix =
        let canonical = binary 16384 root (prefix + "CanonicalBase64")

        try
            let signature = binary 64 root (prefix + "SignatureBase64")

            if signature.Length <> 64 then
                CryptographicOperations.ZeroMemory(signature)
                invalidOp "Private adoption signature is invalid."

            {
                Canonical = canonical
                Signature = signature
            }
        with _ ->
            CryptographicOperations.ZeroMemory(canonical)
            reraise ()

    let zero (submission: CopyAdoptionSubmission) =
        for item in [ submission.Custodian; submission.Registry; submission.Inspection ] do
            CryptographicOperations.ZeroMemory(item.Canonical)
            CryptographicOperations.ZeroMemory(item.Signature)

    let proposal path =
        match PrivateFileService.readBinary 131072 path with
        | Error _ -> Error DatabaseInputProblem.ManagedCopyFileRefused
        | Ok input ->
            let parse () =
                use documentJson = JsonDocument.Parse(input)
                let root = documentJson.RootElement

                let expected =
                    [
                        "version"
                        "approvalId"
                        "adoptionEventId"
                        "custodianCanonicalBase64"
                        "custodianSignatureBase64"
                        "registryCanonicalBase64"
                        "registrySignatureBase64"
                        "inspectionCanonicalBase64"
                        "inspectionSignatureBase64"
                    ]

                if
                    not (exactNames expected root) || root.GetProperty("version").GetInt32() <> 1
                then
                    invalidOp "Private adoption proposal is invalid."

                let approvalId = guid root "approvalId"
                let adoptionEventId = guid root "adoptionEventId"
                let custodian = document root "custodian"

                try
                    let registry = document root "registry"

                    try
                        let inspection = document root "inspection"

                        Ok
                            {
                                ApprovalId = approvalId
                                AdoptionEventId = adoptionEventId
                                Custodian = custodian
                                Registry = registry
                                Inspection = inspection
                            }
                    with _ ->
                        CryptographicOperations.ZeroMemory(registry.Canonical)
                        CryptographicOperations.ZeroMemory(registry.Signature)
                        reraise ()
                with _ ->
                    CryptographicOperations.ZeroMemory(custodian.Canonical)
                    CryptographicOperations.ZeroMemory(custodian.Signature)
                    reraise ()

            try
                try
                    parse ()
                with _ ->
                    Error DatabaseInputProblem.ManagedCopyFileRefused
            finally
                CryptographicOperations.ZeroMemory(input)

    let mapping path =
        match PrivateFileService.readBinary 8192 path with
        | Error _ -> None
        | Ok input ->
            let parse () =
                use documentJson = JsonDocument.Parse(input)
                let root = documentJson.RootElement

                if
                    not (
                        exactNames [ "version"; "copyId"; "caseId"; "custodianId"; "location" ] root
                    )
                    || root.GetProperty("version").GetInt32() <> 1
                then
                    None
                else
                    let custodian = text root "custodianId"
                    let location = text root "location"

                    if
                        String.IsNullOrWhiteSpace custodian || String.IsNullOrWhiteSpace location
                    then
                        None
                    else
                        Some(guid root "copyId", guid root "caseId", custodian, location)

            try
                try
                    parse ()
                with _ ->
                    None
            finally
                CryptographicOperations.ZeroMemory(input)
