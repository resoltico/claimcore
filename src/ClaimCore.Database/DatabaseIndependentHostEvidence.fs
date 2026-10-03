namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type internal IndependentHostDocuments =
    {
        Topology: byte array
        TopologySignature: byte array
        Aggregate: byte array
        AggregateSignature: byte array
        AggregatePublicKey: byte array
        RolePublicKeys: Map<string, byte array>
    }

/// Owner-private delivery paths are selectable; the signing keys are not trusted until their
/// exact digests are checked against the root-signed topology.
module internal DatabaseIndependentHostEvidence =
    let private directoryName = "CLAIMCORE_DEPLOYMENT_EVIDENCE_DIR"

    let requireIndependentKeys (documents: IndependentHostDocuments) =
        let roles =
            set [ "archive"; "checkpoint"; "key"; "primary"; "witness"; "old-writer-fence" ]

        if (documents.RolePublicKeys |> Map.keys |> Set.ofSeq) <> roles then
            invalidOp "Independent signing-key roles are incomplete."

        let identities =
            documents.AggregatePublicKey
            :: (documents.RolePublicKeys |> Map.toList |> List.map snd)
            |> List.map (DatabaseIndependentHostJson.rawPublicKey >> Convert.ToHexStringLower)

        if (identities |> Set.ofList |> Set.count) <> identities.Length then
            invalidOp "Independent roles reuse a signing key."

    let private read maximum exact directory name =
        let path = Path.Combine(directory, name)

        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 && (not exact || bytes.Length = maximum) -> bytes
        | _ -> invalidOp "Owner-private independent-host evidence is unavailable."

    let private zero (bytes: byte array) =
        if not (isNull (box bytes)) then
            CryptographicOperations.ZeroMemory(bytes)

    let dispose (documents: IndependentHostDocuments) =
        zero documents.Topology
        zero documents.TopologySignature
        zero documents.Aggregate
        zero documents.AggregateSignature
        zero documents.AggregatePublicKey
        documents.RolePublicKeys |> Map.iter (fun _ bytes -> zero bytes)

    let load () =
        let directory =
            Environment.GetEnvironmentVariable(directoryName)
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () ->
                invalidOp "Owner-private independent-host evidence directory is unavailable.")

        match PrivateFileService.requirePrivateDirectory directory with
        | Ok() -> ()
        | Error _ -> invalidOp "Owner-private independent-host evidence directory is unsafe."

        let roles =
            [ "archive"; "checkpoint"; "key"; "primary"; "witness"; "old-writer-fence" ]

        let mutable loaded: byte array list = []

        let retain maximum exact name =
            let bytes = read maximum exact directory name
            loaded <- bytes :: loaded
            bytes

        try
            let topology = retain 65536 false "topology.json"
            let topologySignature = retain 64 true "topology.sig"
            let aggregate = retain 131072 false "aggregate.json"
            let aggregateSignature = retain 64 true "aggregate.sig"
            let aggregatePublicKey = retain 512 false "aggregate.pub"

            let rolePublicKeys =
                roles
                |> List.map (fun role -> role, retain 512 false (role + ".pub"))
                |> Map.ofList

            {
                Topology = topology
                TopologySignature = topologySignature
                Aggregate = aggregate
                AggregateSignature = aggregateSignature
                AggregatePublicKey = aggregatePublicKey
                RolePublicKeys = rolePublicKeys
            }
        with _ ->
            loaded |> List.iter zero
            reraise ()
