namespace ClaimCore.Database

open System
open System.Text.Json

/// An installation-specific topology is signed by a source-pinned independent root.
module internal DatabaseIndependentHostTopology =
    let private names (source: string) = source.Split('|') |> Array.toList

    let private fields =
        names (
            "format|topologyId|installationId|lineageId|epoch|writerGeneration|w1Sequence|w1Hash|"
            + "publicationManifestSha256|deploymentVerifierSigningKeyId|deploymentVerifierHolderActorId|"
            + "deploymentVerifierPublicKeySha256|rolePins|fenceObserverPin|issuedAt|validUntil"
        )

    let private roleFields =
        names
            "role|probePublicKeySha256|machineHash|storageHash|adminActorId|hostKeyId|sshHostKeySha256"

    let private observerFields =
        roleFields
        @ names (
            "oldEndpointId|oldEndpointAddressSha256|oldPrimaryRoleOid|oldWitnessRoleOid|"
            + "oldPrimaryCredentialSha256|oldWitnessCredentialSha256|"
            + "primarySessionSetSha256|witnessSessionSetSha256"
        )

    let private roles = IndependentHostRoles.hosts

    let private role (value: JsonElement) =
        DatabaseIndependentHostJson.exact roleFields value

        {
            Role = DatabaseIndependentHostJson.text "role" value
            PublicKeySha256 = DatabaseIndependentHostJson.digest "probePublicKeySha256" value
            MachineHash = DatabaseIndependentHostJson.digest "machineHash" value
            StorageHash = DatabaseIndependentHostJson.digest "storageHash" value
            AdminActorId = DatabaseIndependentHostJson.uuid "adminActorId" value
            HostKeyId = DatabaseIndependentHostJson.uuid "hostKeyId" value
            SshHostKeySha256 = DatabaseIndependentHostJson.digest "sshHostKeySha256" value
        }

    let private observer (value: JsonElement) =
        DatabaseIndependentHostJson.exact observerFields value

        let pin =
            {
                Role = DatabaseIndependentHostJson.text "role" value
                PublicKeySha256 = DatabaseIndependentHostJson.digest "probePublicKeySha256" value
                MachineHash = DatabaseIndependentHostJson.digest "machineHash" value
                StorageHash = DatabaseIndependentHostJson.digest "storageHash" value
                AdminActorId = DatabaseIndependentHostJson.uuid "adminActorId" value
                HostKeyId = DatabaseIndependentHostJson.uuid "hostKeyId" value
                SshHostKeySha256 = DatabaseIndependentHostJson.digest "sshHostKeySha256" value
            }

        let oid name =
            let number = DatabaseIndependentHostJson.integer name value

            if number < 1L || number > int64 UInt32.MaxValue then
                invalidOp "Independent old-writer role identity is invalid."

            number

        {
            Role = pin
            OldEndpointId = DatabaseIndependentHostJson.uuid "oldEndpointId" value
            OldEndpointAddressSha256 =
                DatabaseIndependentHostJson.digest "oldEndpointAddressSha256" value
            OldPrimaryRoleOid = oid "oldPrimaryRoleOid"
            OldWitnessRoleOid = oid "oldWitnessRoleOid"
            OldPrimaryCredentialSha256 =
                DatabaseIndependentHostJson.digest "oldPrimaryCredentialSha256" value
            OldWitnessCredentialSha256 =
                DatabaseIndependentHostJson.digest "oldWitnessCredentialSha256" value
            PrimarySessionSetSha256 =
                DatabaseIndependentHostJson.digest "primarySessionSetSha256" value
            WitnessSessionSetSha256 =
                DatabaseIndependentHostJson.digest "witnessSessionSetSha256" value
        }

    let roleKeysIndependent
        (pins: IndependentRolePin list)
        (fence: IndependentFencePin)
        aggregatePublicKeySha256
        =
        let all = pins @ [ fence.Role ]

        let unique select =
            let values = all |> List.map select
            values.Length = (values |> Set.ofList |> Set.count)

        unique _.PublicKeySha256
        && (all |> List.forall (fun pin -> pin.PublicKeySha256 <> aggregatePublicKeySha256))
        && unique _.MachineHash
        && unique _.StorageHash
        && unique _.AdminActorId
        && unique _.HostKeyId

    let private matchesAuthority
        (value: JsonElement)
        (publication: TrustedRestorePublication)
        (tail: FencedTailClaims)
        epoch
        generation
        w1
        w1Hash
        publicationSha
        =
        DatabaseIndependentHostJson.uuid "installationId" value = tail.InstallationId
        && DatabaseIndependentHostJson.uuid "lineageId" value = tail.LineageId
        && epoch = tail.Epoch
        && generation = tail.NewGeneration
        && w1 = tail.W1Sequence
        && w1Hash = tail.W1Hash
        && publicationSha = publication.ManifestSha256
        && tail.InstallationId = publication.InstallationId
        && tail.LineageId = publication.LineageId
        && epoch = publication.Epoch
        && generation = publication.WriterGeneration + 1L
        && w1 > publication.WitnessCutoff

    let private project
        canonical
        (tail: FencedTailClaims)
        epoch
        generation
        w1
        w1Hash
        publicationSha
        aggregateKeySha
        (pins: IndependentRolePin list)
        (fence: IndependentFencePin)
        expires
        (value: JsonElement)
        =
        DatabaseIndependentHostJson.uuid "topologyId" value |> ignore

        {
            Digest = DatabaseIndependentHostJson.sha256 canonical
            InstallationId = tail.InstallationId
            LineageId = tail.LineageId
            Epoch = epoch
            WriterGeneration = generation
            W1Sequence = w1
            W1Hash = w1Hash
            PublicationManifestSha256 = publicationSha
            AggregateSigningKeyId =
                DatabaseIndependentHostJson.uuid "deploymentVerifierSigningKeyId" value
            AggregateHolderActorId =
                DatabaseIndependentHostJson.uuid "deploymentVerifierHolderActorId" value
            AggregatePublicKeySha256 = aggregateKeySha
            Pins = pins
            Observer = fence
            ValidUntil = expires
        }

    let private validTopology
        (value: JsonElement)
        (publication: TrustedRestorePublication)
        (tail: FencedTailClaims)
        (pins: IndependentRolePin list)
        (fence: IndependentFencePin)
        epoch
        generation
        w1
        w1Hash
        publicationSha
        aggregateKeySha
        (issued: DateTimeOffset)
        (expires: DateTimeOffset)
        (now: DateTimeOffset)
        =
        DatabaseIndependentHostJson.text "format" value = "claimcore-deployment-topology-1"
        && (pins |> List.map _.Role) = roles
        && fence.Role.Role = "old-writer-fence"
        && roleKeysIndependent pins fence aggregateKeySha
        && matchesAuthority value publication tail epoch generation w1 w1Hash publicationSha
        && issued <= now
        && now < expires
        && expires <= issued.AddDays(7.)

    let private pinsAndFence (value: JsonElement) =
        let items = value.GetProperty("rolePins")

        if items.ValueKind <> JsonValueKind.Array then
            invalidOp "Independent host roles are invalid."

        items.EnumerateArray() |> Seq.map role |> Seq.toList,
        observer (value.GetProperty("fenceObserverPin"))

    let private authorityFields (value: JsonElement) =
        DatabaseIndependentHostJson.integer "epoch" value,
        DatabaseIndependentHostJson.integer "writerGeneration" value,
        DatabaseIndependentHostJson.integer "w1Sequence" value,
        DatabaseIndependentHostJson.digest "w1Hash" value,
        DatabaseIndependentHostJson.digest "publicationManifestSha256" value,
        DatabaseIndependentHostJson.digest "deploymentVerifierPublicKeySha256" value

    let private parseTopology
        (value: JsonElement)
        (publication: TrustedRestorePublication)
        (tail: FencedTailClaims)
        (canonical: byte array)
        (now: DateTimeOffset)
        =
        DatabaseIndependentHostJson.exact fields value
        let issued = DatabaseIndependentHostJson.instant "issuedAt" value
        let expires = DatabaseIndependentHostJson.instant "validUntil" value
        let pins, fence = pinsAndFence value

        let epoch, generation, w1, w1Hash, publicationSha, aggregateKeySha =
            authorityFields value

        if
            validTopology
                value
                publication
                tail
                pins
                fence
                epoch
                generation
                w1
                w1Hash
                publicationSha
                aggregateKeySha
                issued
                expires
                now
        then
            Some(
                project
                    canonical
                    tail
                    epoch
                    generation
                    w1
                    w1Hash
                    publicationSha
                    aggregateKeySha
                    pins
                    fence
                    expires
                    value
            )
        else
            None

    let verifyWithRoot
        (rootKey: byte array)
        (publication: TrustedRestorePublication)
        (tail: FencedTailClaims)
        (canonical: byte array)
        (signature: byte array)
        (now: DateTimeOffset)
        : IndependentHostTopology option =
        try
            use document = DatabaseIndependentHostJson.signed 65536 rootKey canonical signature
            parseTopology document.RootElement publication tail canonical now
        with _ ->
            None
