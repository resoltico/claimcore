namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Exact owner-private signed documents; public command arguments contain only this file path.
module internal DatabaseExternalPublicationInputs =
    let private names =
        Set.ofList
            [
                "version"
                "publicationId"
                "registryCanonicalBase64"
                "registrySignatureBase64"
                "inspectionCanonicalBase64"
                "inspectionSignatureBase64"
            ]

    let private stringValue (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            invalidOp "Private publication input is invalid."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Private publication input is invalid.")

    let private bytes maximum (root: JsonElement) (name: string) =
        let encoded = stringValue root name
        let value = Convert.FromBase64String(encoded)

        if
            value.Length < 1
            || value.Length > maximum
            || Convert.ToBase64String(value) <> encoded
        then
            CryptographicOperations.ZeroMemory(value)
            invalidOp "Private publication input is invalid."

        value

    let private signed (root: JsonElement) prefix =
        let canonical = bytes 16384 root (prefix + "CanonicalBase64")

        try
            let signature = bytes 64 root (prefix + "SignatureBase64")

            if signature.Length <> 64 then
                CryptographicOperations.ZeroMemory(signature)
                invalidOp "Private publication signature is invalid."

            {
                Canonical = canonical
                Signature = signature
            }
        with _ ->
            CryptographicOperations.ZeroMemory(canonical)
            reraise ()

    let zero (submission: ExternalCopyPublicationSubmission) =
        for document in [ submission.Registry; submission.Inspection ] do
            CryptographicOperations.ZeroMemory(document.Canonical)
            CryptographicOperations.ZeroMemory(document.Signature)

    let private parse (source: byte array) =
        use document = JsonDocument.Parse(source)
        let root = document.RootElement
        let properties = root.EnumerateObject() |> Seq.map _.Name |> Seq.toList

        if
            root.ValueKind <> JsonValueKind.Object
            || properties.Length <> names.Count
            || Set.ofList properties <> names
            || root.GetProperty("version").GetInt32() <> 1
        then
            invalidOp "Private publication proposal is invalid."

        let identifier = stringValue root "publicationId"

        let publicationId =
            match Guid.TryParseExact(identifier, "D") with
            | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = identifier -> parsed
            | _ -> invalidOp "Private publication identity is invalid."

        let registry = signed root "registry"

        try
            let inspection = signed root "inspection"

            {
                PublicationId = publicationId
                Registry = registry
                Inspection = inspection
            }
        with _ ->
            CryptographicOperations.ZeroMemory(registry.Canonical)
            CryptographicOperations.ZeroMemory(registry.Signature)
            reraise ()

    let proposal path =
        match PrivateFileService.readBinary 131072 path with
        | Error _ -> Error DatabaseInputProblem.ManagedCopyFileRefused
        | Ok source ->
            try
                try
                    parse source |> Ok
                with _ ->
                    Error DatabaseInputProblem.ManagedCopyFileRefused
            finally
                CryptographicOperations.ZeroMemory(source)
