namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal ManagedCopyPhysicalInputs =
    {
        CopyId: Guid
        ObjectPath: string
        MaximumObjectBytes: int64
        Proof: byte array
        Signature: byte array
        CommitmentKeyPath: string
    }

/// One owner-private fixed proof location; no claimant path or key bytes enter argv/output.
module internal DatabaseManagedCopyPhysicalInputs =
    let private names =
        set
            [
                "format"
                "copyId"
                "objectPath"
                "maximumObjectBytes"
                "proofFile"
                "signatureFile"
                "commitmentKeyFile"
            ]

    let private validPath (value: string) =
        Path.IsPathFullyQualified(value)
        && value = Path.GetFullPath(value)
        && not (value.Split(Path.DirectorySeparatorChar) |> Array.exists ((=) ".."))

    let private requiredText (root: JsonElement) (name: string) : string =
        let item = root.GetProperty(name)

        if item.ValueKind <> JsonValueKind.String then
            invalidOp "Physical proof path is invalid."

        item.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Physical proof path is invalid.")

    let private exactId (value: string) =
        let parsed = Guid.ParseExact(value, "D")

        if parsed = Guid.Empty || parsed.ToString("D") <> value then
            invalidOp "Physical copy identity is invalid."

        parsed

    let private decode (root: JsonElement) =
        let properties = root.EnumerateObject() |> Seq.toArray

        if
            root.ValueKind <> JsonValueKind.Object
            || properties.Length <> names.Count
            || (properties |> Seq.map _.Name |> Set.ofSeq) <> names
            || requiredText root "format" <> "claimcore-managed-copy-physical-input-1"
        then
            invalidOp "Physical proof input format is invalid."

        let objectPath = requiredText root "objectPath"
        let proofPath = requiredText root "proofFile"
        let signaturePath = requiredText root "signatureFile"
        let commitmentKeyPath = requiredText root "commitmentKeyFile"
        let maximum = root.GetProperty("maximumObjectBytes").GetInt64()

        if
            [ objectPath; proofPath; signaturePath; commitmentKeyPath ]
            |> List.exists (validPath >> not)
            || maximum < 1L
            || maximum > (1L <<< 50)
        then
            invalidOp "Physical proof private path policy is invalid."

        let proof =
            PrivateFileService.readBinary 16384 proofPath
            |> Result.defaultWith (fun _ -> invalidOp "Physical proof file is unavailable.")

        try
            let signature =
                PrivateFileService.readBinary 64 signaturePath
                |> Result.defaultWith (fun _ -> invalidOp "Physical signature file is unavailable.")

            if signature.Length <> 64 then
                CryptographicOperations.ZeroMemory(signature)
                invalidOp "Physical signature is invalid."

            {
                CopyId = requiredText root "copyId" |> exactId
                ObjectPath = objectPath
                MaximumObjectBytes = maximum
                Proof = proof
                Signature = signature
                CommitmentKeyPath = commitmentKeyPath
            }
        with _ ->
            CryptographicOperations.ZeroMemory(proof)
            reraise ()

    let load () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_COPY_PHYSICAL_INPUT_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.PhysicalCopyProofFileRefused
        | path ->
            match PrivateFileService.readBinary 8192 path with
            | Error _ -> Error DatabaseInputProblem.PhysicalCopyProofFileRefused
            | Ok bytes ->
                try
                    try
                        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                        Ok(decode document.RootElement)
                    with _ ->
                        Error DatabaseInputProblem.PhysicalCopyProofFileRefused
                finally
                    CryptographicOperations.ZeroMemory(bytes)
