namespace ClaimCore.Database

open System
open System.Text
open System.Text.Json

[<NoEquality; NoComparison>]
type internal IndependentHostAggregate =
    {
        Digest: string
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
        ProbeEvidenceSha256: string
        TopologySha256: string
    }

/// Native product verification of the aggregate and all six exact signed child documents.
module internal DatabaseIndependentHostAggregate =
    let private fields =
        [
            "format"
            "source"
            "scope"
            "installationId"
            "lineageId"
            "epoch"
            "writerGeneration"
            "nonce"
            "reportSha256"
            "fenceReportSha256"
            "supplementSha256"
            "finalWalObjectSha256"
            "w1Sequence"
            "w1Hash"
            "publicationManifestSha256"
            "topologyManifestSha256"
            "verifierBinarySha256"
            "probeSetSha256"
            "probes"
            "oldWriterFenceObservation"
            "checkedAt"
            "validUntil"
            "signingKeyId"
            "signerHolderActorId"
            "realDataReady"
        ]

    let private roles = [ "archive"; "checkpoint"; "key"; "primary"; "witness" ]

    let checkedAt (bytes: byte array) =
        use document = DatabaseIndependentHostJson.canonical 131072 bytes
        DatabaseIndependentHostJson.instant "checkedAt" document.RootElement

    let private exactProbeSet (probes: JsonElement) expected =
        if probes.ValueKind <> JsonValueKind.Array || probes.GetArrayLength() <> 5 then
            invalidOp "Independent host probe set is incomplete."

        let raw = Encoding.ASCII.GetBytes(probes.GetRawText() + "\n")

        if DatabaseIndependentHostJson.sha256 raw <> expected then
            invalidOp "Independent host probe set digest diverged."

    let private links
        (proof: JsonElement)
        (topology: IndependentHostTopology)
        (publication: TrustedRestorePublication)
        (backup: RestoreReportClaims)
        (fence: WriterFenceClaims)
        (tail: FencedTailClaims)
        reportSha
        fenceSha
        supplementSha
        =
        DatabaseIndependentHostJson.text "format" proof = "claimcore-independent-host-proof-1"
        && DatabaseIndependentHostJson.text "source" proof = "ClaimCore.DeploymentVerifier"
        && DatabaseIndependentHostJson.text "scope" proof = "full"
        && not (DatabaseIndependentHostJson.flag "realDataReady" proof)
        && DatabaseIndependentHostJson.uuid "installationId" proof = tail.InstallationId
        && DatabaseIndependentHostJson.uuid "lineageId" proof = tail.LineageId
        && DatabaseIndependentHostJson.integer "epoch" proof = tail.Epoch
        && DatabaseIndependentHostJson.integer "writerGeneration" proof = tail.NewGeneration
        && DatabaseIndependentHostJson.integer "w1Sequence" proof = tail.W1Sequence
        && DatabaseIndependentHostJson.digest "w1Hash" proof = tail.W1Hash
        && DatabaseIndependentHostJson.digest "reportSha256" proof = reportSha
        && DatabaseIndependentHostJson.digest "fenceReportSha256" proof = fenceSha
        && DatabaseIndependentHostJson.digest "supplementSha256" proof = supplementSha
        && DatabaseIndependentHostJson.digest "finalWalObjectSha256" proof =
            DatabaseRestoreWalObjectDigest.compute tail.WalObjects
        && DatabaseIndependentHostJson.digest "publicationManifestSha256" proof =
            publication.ManifestSha256
        && DatabaseIndependentHostJson.digest "topologyManifestSha256" proof = topology.Digest
        && DatabaseIndependentHostJson.digest "verifierBinarySha256" proof =
            publication.VerifierBinarySha256
        && DatabaseIndependentHostJson.uuid "signingKeyId" proof = topology.AggregateSigningKeyId
        && DatabaseIndependentHostJson.uuid "signerHolderActorId" proof =
            topology.AggregateHolderActorId
        && backup.VerifierBinarySha256 = publication.VerifierBinarySha256
        && backup.InstallationId = tail.InstallationId
        && backup.LineageId = tail.LineageId
        && backup.Epoch = tail.Epoch
        && backup.Scope = "full"
        && tail.Scope = "full"
        && tail.ReportSha256 = reportSha
        && tail.FenceReportSha256 = fenceSha
        && fence.InstallationId = tail.InstallationId
        && fence.LineageId = tail.LineageId
        && fence.Epoch = tail.Epoch
        && fence.NewGeneration = tail.NewGeneration
        && fence.ReportSha256 = reportSha

    let private childExpiry
        (proof: JsonElement)
        (documents: IndependentHostDocuments)
        (topology: IndependentHostTopology)
        (backup: RestoreReportClaims)
        (fence: WriterFenceClaims)
        (tail: FencedTailClaims)
        reportSha
        fenceSha
        supplementSha
        aggregateAt
        (now: DateTimeOffset option)
        =
        let nonce = DatabaseIndependentHostJson.digest "nonce" proof
        let probes = proof.GetProperty("probes")
        let expectedSet = DatabaseIndependentHostJson.digest "probeSetSha256" proof
        exactProbeSet probes expectedSet
        let entries = probes.EnumerateArray() |> Seq.toList

        let expiries =
            (roles, entries, topology.Pins)
            |||> List.map3 (fun role entry pin ->
                if pin.Role <> role then
                    invalidOp "Independent role order diverged."

                let publicKey = documents.RolePublicKeys[role]

                let _, expiry =
                    DatabaseIndependentHostChildren.verify
                        entry
                        pin
                        publicKey
                        nonce
                        supplementSha
                        backup
                        tail
                        aggregateAt
                        now

                expiry)

        let observerKey = documents.RolePublicKeys["old-writer-fence"]

        let _, observerExpiry =
            DatabaseIndependentHostObserver.verify
                (proof.GetProperty("oldWriterFenceObservation"))
                topology.Observer
                observerKey
                nonce
                reportSha
                fenceSha
                fence
                tail
                aggregateAt
                now

        observerExpiry :: expiries |> List.min

    let private signedAggregate
        (documents: IndependentHostDocuments)
        (topology: IndependentHostTopology)
        =
        if
            DatabaseIndependentHostJson.sha256 documents.AggregatePublicKey
            <> topology.AggregatePublicKeySha256
        then
            invalidOp "Independent aggregate key differs from root-signed topology."

        let key = DatabaseIndependentHostJson.rawPublicKey documents.AggregatePublicKey

        DatabaseIndependentHostJson.signed
            131072
            key
            documents.Aggregate
            documents.AggregateSignature

    let private validWindow
        (aggregateAt: DateTimeOffset)
        (expires: DateTimeOffset)
        (tail: FencedTailClaims)
        (now: DateTimeOffset option)
        =
        aggregateAt <= expires
        && expires <= aggregateAt.AddSeconds(90.)
        && (match now with
            | Some current -> aggregateAt <= current && current < expires
            | None -> aggregateAt >= tail.CheckedAt && aggregateAt < tail.ValidUntil)

    let verify
        (documents: IndependentHostDocuments)
        (topology: IndependentHostTopology)
        (publication: TrustedRestorePublication)
        (backup: RestoreReportClaims)
        (fence: WriterFenceClaims)
        (tail: FencedTailClaims)
        reportSha
        fenceSha
        supplementSha
        (now: DateTimeOffset option)
        =
        use document = signedAggregate documents topology

        let proof = document.RootElement
        DatabaseIndependentHostJson.exact fields proof
        let aggregateAt = DatabaseIndependentHostJson.instant "checkedAt" proof
        let expires = DatabaseIndependentHostJson.instant "validUntil" proof

        if
            not (
                links proof topology publication backup fence tail reportSha fenceSha supplementSha
            )
            || not (validWindow aggregateAt expires tail now)
        then
            invalidOp "Independent aggregate is invalid or stale."

        let children =
            childExpiry
                proof
                documents
                topology
                backup
                fence
                tail
                reportSha
                fenceSha
                supplementSha
                aggregateAt
                now

        {
            Digest = DatabaseIndependentHostJson.sha256 documents.Aggregate
            CheckedAt = aggregateAt
            ValidUntil = min expires children
            ProbeEvidenceSha256 = DatabaseIndependentHostJson.sha256 documents.Aggregate
            TopologySha256 = topology.Digest
        }
