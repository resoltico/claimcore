namespace ClaimCore.Postgres

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open System.Text.Json

[<NoEquality; NoComparison>]
type internal WriterHandoffAbort =
    {
        HandoffId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        OldGeneration: int64
        PrepareSequence: int64
        PrepareHash: byte array
        PrepareCanonicalSha256: byte array
        OldCapabilitySha256: byte array
        NewCapabilitySha256: byte array
        AbortSigningKeyOneId: Guid
        AbortSigningKeyTwoId: Guid
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        OwnerOneActorId: Guid
        OwnerTwoActorId: Guid
        OwnerOneGrantRevision: int64
        OwnerTwoGrantRevision: int64
        ExpectedAuthorityRevision: int64
        ValidUntil: DateTimeOffset
    }

/// One byte-exact abort candidate is signed by two distinct pre-registered human owners.
module internal WriterHandoffAbort =
    let encode (value: WriterHandoffAbort) =
        let fields = SortedDictionary<string, objnull>(StringComparer.Ordinal)
        let put name item = fields.Add(name, box item)
        let uuid (id: Guid) = id.ToString("D")
        let hex (bytes: byte array) = Convert.ToHexStringLower(bytes)

        let time =
            value.ValidUntil
                .ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)

        put "format" "claimcore-writer-handoff-1"
        put "stage" "ABORT"
        put "handoffId" (uuid value.HandoffId)
        put "installationId" (uuid value.InstallationId)
        put "lineageId" (uuid value.LineageId)
        put "epoch" value.Epoch
        put "oldGeneration" value.OldGeneration
        put "prepareSequence" value.PrepareSequence
        put "prepareHash" (hex value.PrepareHash)
        put "prepareCanonicalSha256" (hex value.PrepareCanonicalSha256)
        put "oldCapabilitySha256" (hex value.OldCapabilitySha256)
        put "newCapabilitySha256" (hex value.NewCapabilitySha256)
        put "abortSigningKeyOneId" (uuid value.AbortSigningKeyOneId)
        put "abortSigningKeyTwoId" (uuid value.AbortSigningKeyTwoId)
        put "approvalOneId" (uuid value.ApprovalOneId)
        put "approvalTwoId" (uuid value.ApprovalTwoId)
        put "ownerOneActorId" (uuid value.OwnerOneActorId)
        put "ownerTwoActorId" (uuid value.OwnerTwoActorId)
        put "ownerOneGrantRevision" value.OwnerOneGrantRevision
        put "ownerTwoGrantRevision" value.OwnerTwoGrantRevision
        put "expectedAuthorityRevision" value.ExpectedAuthorityRevision
        put "validUntil" time
        Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

    let private fields =
        set
            [
                "format"
                "stage"
                "handoffId"
                "installationId"
                "lineageId"
                "epoch"
                "oldGeneration"
                "prepareSequence"
                "prepareHash"
                "prepareCanonicalSha256"
                "oldCapabilitySha256"
                "newCapabilitySha256"
                "abortSigningKeyOneId"
                "abortSigningKeyTwoId"
                "approvalOneId"
                "approvalTwoId"
                "ownerOneActorId"
                "ownerTwoActorId"
                "ownerOneGrantRevision"
                "ownerTwoGrantRevision"
                "expectedAuthorityRevision"
                "validUntil"
            ]

    let private decode (root: JsonElement) =
        {
            HandoffId = WriterHandoffCanonical.uuid root "handoffId"
            InstallationId = WriterHandoffCanonical.uuid root "installationId"
            LineageId = WriterHandoffCanonical.uuid root "lineageId"
            Epoch = WriterHandoffCanonical.number root "epoch"
            OldGeneration = WriterHandoffCanonical.number root "oldGeneration"
            PrepareSequence = WriterHandoffCanonical.number root "prepareSequence"
            PrepareHash = WriterHandoffCanonical.digest root "prepareHash"
            PrepareCanonicalSha256 = WriterHandoffCanonical.digest root "prepareCanonicalSha256"
            OldCapabilitySha256 = WriterHandoffCanonical.digest root "oldCapabilitySha256"
            NewCapabilitySha256 = WriterHandoffCanonical.digest root "newCapabilitySha256"
            AbortSigningKeyOneId = WriterHandoffCanonical.uuid root "abortSigningKeyOneId"
            AbortSigningKeyTwoId = WriterHandoffCanonical.uuid root "abortSigningKeyTwoId"
            ApprovalOneId = WriterHandoffCanonical.uuid root "approvalOneId"
            ApprovalTwoId = WriterHandoffCanonical.uuid root "approvalTwoId"
            OwnerOneActorId = WriterHandoffCanonical.uuid root "ownerOneActorId"
            OwnerTwoActorId = WriterHandoffCanonical.uuid root "ownerTwoActorId"
            OwnerOneGrantRevision = WriterHandoffCanonical.number root "ownerOneGrantRevision"
            OwnerTwoGrantRevision = WriterHandoffCanonical.number root "ownerTwoGrantRevision"
            ExpectedAuthorityRevision =
                WriterHandoffCanonical.number root "expectedAuthorityRevision"
            ValidUntil = WriterHandoffCanonical.time root "validUntil"
        }

    let parse canonical =
        WriterHandoffCanonical.shape canonical fields "ABORT"
        |> Option.bind (fun root ->
            try
                let value = decode root

                if
                    value.Epoch > 0L
                    && value.OldGeneration > 0L
                    && value.PrepareSequence > 0L
                    && value.AbortSigningKeyOneId <> value.AbortSigningKeyTwoId
                    && value.ApprovalOneId <> value.ApprovalTwoId
                    && value.OwnerOneActorId <> value.OwnerTwoActorId
                    && value.OwnerOneGrantRevision > 0L
                    && value.OwnerTwoGrantRevision > 0L
                    && value.ExpectedAuthorityRevision > 0L
                then
                    Some value
                else
                    None
            with _ ->
                None)
