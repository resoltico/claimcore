module ClaimCore.IntegrationTests.IndependentHostTopologyTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Text
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.IntegrationTests.IndependentHostProbeFixture

let private sorted (fields: (string * (obj | null)) list) =
    let result = SortedDictionary<string, obj | null>(StringComparer.Ordinal)

    for name, value in fields do
        result.Add(name, value)

    result

let private timestamp (value: DateTimeOffset) =
    value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")

let private rolePin role index =
    sorted
        [
            "role", box role
            "probePublicKeySha256", box (sprintf "%064x" index)
            "machineHash", box (sprintf "%064x" (index + 10))
            "storageHash", box (sprintf "%064x" (index + 20))
            "adminActorId", box (Guid.NewGuid().ToString("D"))
            "hostKeyId", box (Guid.NewGuid().ToString("D"))
            "sshHostKeySha256", box (sprintf "%064x" (index + 30))
        ]

let private observerPin () =
    let observer = rolePin "old-writer-fence" 6
    observer.Add("oldEndpointId", box (Guid.NewGuid().ToString("D")))
    observer.Add("oldEndpointAddressSha256", box (String.replicate 64 "a"))
    observer.Add("oldPrimaryRoleOid", box 101L)
    observer.Add("oldWitnessRoleOid", box 102L)
    observer.Add("oldPrimaryCredentialSha256", box (String.replicate 64 "b"))
    observer.Add("oldWitnessCredentialSha256", box (String.replicate 64 "c"))
    observer.Add("primarySessionSetSha256", box (String.replicate 64 "d"))
    observer.Add("witnessSessionSetSha256", box (String.replicate 64 "e"))
    observer

let private publication (tail: FencedTailClaims) =
    {
        ManifestSha256 = String.replicate 64 "9"
        VerifierBinarySha256 = String.replicate 64 "8"
        ReportSignerKeyId = Guid.NewGuid()
        CheckpointSignerKeyId = tail.CheckpointSignerKeyId
        InstallationId = tail.InstallationId
        LineageId = tail.LineageId
        Epoch = tail.Epoch
        WriterGeneration = tail.OldGeneration
        WitnessCutoff = tail.W1Sequence - 1L
        WitnessCutoffHash = String.replicate 64 "7"
    }

let private signedTopology
    root
    (now: DateTimeOffset)
    (tail: FencedTailClaims)
    duplicateAggregateKey
    =
    let roles = [ "archive"; "checkpoint"; "key"; "primary"; "witness" ]
    let pins = roles |> List.mapi (fun index role -> rolePin role (index + 1))

    let aggregateKey =
        if duplicateAggregateKey then
            pins[0]["probePublicKeySha256"]
        else
            box (sprintf "%064x" 7)

    let body =
        sorted
            [
                "format", box "claimcore-deployment-topology-1"
                "topologyId", box (Guid.NewGuid().ToString("D"))
                "installationId", box (tail.InstallationId.ToString("D"))
                "lineageId", box (tail.LineageId.ToString("D"))
                "epoch", box tail.Epoch
                "writerGeneration", box tail.NewGeneration
                "w1Sequence", box tail.W1Sequence
                "w1Hash", box tail.W1Hash
                "publicationManifestSha256", box (String.replicate 64 "9")
                "deploymentVerifierSigningKeyId", box (Guid.NewGuid().ToString("D"))
                "deploymentVerifierHolderActorId", box (Guid.NewGuid().ToString("D"))
                "deploymentVerifierPublicKeySha256", aggregateKey
                "rolePins", box (pins |> List.toArray)
                "fenceObserverPin", box (observerPin ())
                "issuedAt", box (timestamp (now.AddMinutes(-1.)))
                "validUntil", box (timestamp (now.AddHours(1.)))
            ]

    let canonical =
        Array.append (JsonSerializer.SerializeToUtf8Bytes(body)) [| byte '\n' |]

    canonical, SignatureAlgorithm.Ed25519.Sign(root, canonical)

let private topologyPin =
    testCase
        "[CC-BACKUP-001] root-signed topology refuses an aggregate key reused by a host"
        (fun _ ->
            let now =
                DateTimeOffset.UtcNow
                    .ToUniversalTime()
                    .AddTicks(-(DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerSecond))

            use source = createArchiveProbe now
            use root = Key.Create(SignatureAlgorithm.Ed25519)
            let rawRoot = root.PublicKey.Export(KeyBlobFormat.RawPublicKey)
            let publication = publication source.Tail
            let valid, validSignature = signedTopology root now source.Tail false
            let reused, reusedSignature = signedTopology root now source.Tail true

            Expect.isSome
                (DatabaseIndependentHostTopology.verifyWithRoot
                    rawRoot
                    publication
                    source.Tail
                    valid
                    validSignature
                    now)
                "A distinct aggregate key may be root-pinned."

            Expect.isNone
                (DatabaseIndependentHostTopology.verifyWithRoot
                    rawRoot
                    publication
                    source.Tail
                    reused
                    reusedSignature
                    now)
                "Even a valid root signature cannot permit one key to sign both roles.")

let private rawKeyIndependence () =
    let roles =
        [ "archive"; "checkpoint"; "key"; "primary"; "witness"; "old-writer-fence" ]

    let keys = [ for _ in 0..6 -> Key.Create(SignatureAlgorithm.Ed25519) ]

    let pem (key: Key) width =
        let der =
            Array.append
                (Convert.FromHexString("302a300506032b6570032100"))
                (key.PublicKey.Export(KeyBlobFormat.RawPublicKey))

        let encoded = Convert.ToBase64String(der)
        let lines = encoded |> Seq.chunkBySize width |> Seq.map String |> String.concat "\n"

        Encoding.ASCII.GetBytes(
            "-----BEGIN PUBLIC KEY-----\n" + lines + "\n-----END PUBLIC KEY-----\n"
        )

    try
        let documents: IndependentHostDocuments =
            {
                Topology = [||]
                TopologySignature = [||]
                Aggregate = [||]
                AggregateSignature = [||]
                AggregatePublicKey = pem keys[6] 64
                RolePublicKeys =
                    (roles, keys[..5])
                    ||> List.map2 (fun role key -> role, pem key 64)
                    |> Map.ofList
            }

        DatabaseIndependentHostEvidence.requireIndependentKeys documents
        let rewrapped = pem keys[0] 20
        Expect.notEqual rewrapped documents.RolePublicKeys["archive"] "PEM file identities differ."

        Expect.throws
            (fun () ->
                DatabaseIndependentHostEvidence.requireIndependentKeys
                    { documents with
                        RolePublicKeys = documents.RolePublicKeys.Add("witness", rewrapped)
                    })
            "One raw Ed25519 key cannot supply two independent observations."

        Expect.throws
            (fun () ->
                DatabaseIndependentHostEvidence.requireIndependentKeys
                    { documents with
                        AggregatePublicKey = rewrapped
                    })
            "The aggregate signer cannot reuse an observer key."
    finally
        keys |> List.iter _.Dispose()

let tests =
    testList
        "independent topology signing keys"
        [
            topologyPin
            testCase
                "[CC-BACKUP-001] PEM rewrapping cannot establish independent native signer keys"
                (fun _ -> rawKeyIndependence ())
        ]
