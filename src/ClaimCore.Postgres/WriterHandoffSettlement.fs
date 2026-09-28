namespace ClaimCore.Postgres

open System
open System.Text.Json

[<NoEquality; NoComparison>]
type internal WriterHandoffSettlement =
    {
        HandoffId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        OldGeneration: int64
        NewGeneration: int64
        PrepareSequence: int64
        PrepareHash: byte array
        PrepareCanonicalSha256: byte array
        NewCapabilitySha256: byte array
        CheckpointSigningKeyId: Guid
        FenceReportSha256: byte array
        InventorySha256: byte array
        RestoreReportSha256: byte array
        ApprovalOneId: Guid
        ApprovalTwoId: Guid
        ValidUntil: DateTimeOffset
    }

module internal WriterHandoffSettlement =
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
                "newGeneration"
                "prepareSequence"
                "prepareHash"
                "prepareCanonicalSha256"
                "newCapabilitySha256"
                "checkpointSigningKeyId"
                "fenceReportSha256"
                "inventorySha256"
                "restoreReportSha256"
                "approvalOneId"
                "approvalTwoId"
                "validUntil"
            ]

    let private decode (root: JsonElement) =
        {
            HandoffId = WriterHandoffCanonical.uuid root "handoffId"
            InstallationId = WriterHandoffCanonical.uuid root "installationId"
            LineageId = WriterHandoffCanonical.uuid root "lineageId"
            Epoch = WriterHandoffCanonical.number root "epoch"
            OldGeneration = WriterHandoffCanonical.number root "oldGeneration"
            NewGeneration = WriterHandoffCanonical.number root "newGeneration"
            PrepareSequence = WriterHandoffCanonical.number root "prepareSequence"
            PrepareHash = WriterHandoffCanonical.digest root "prepareHash"
            PrepareCanonicalSha256 = WriterHandoffCanonical.digest root "prepareCanonicalSha256"
            NewCapabilitySha256 = WriterHandoffCanonical.digest root "newCapabilitySha256"
            CheckpointSigningKeyId = WriterHandoffCanonical.uuid root "checkpointSigningKeyId"
            FenceReportSha256 = WriterHandoffCanonical.digest root "fenceReportSha256"
            InventorySha256 = WriterHandoffCanonical.digest root "inventorySha256"
            RestoreReportSha256 = WriterHandoffCanonical.digest root "restoreReportSha256"
            ApprovalOneId = WriterHandoffCanonical.uuid root "approvalOneId"
            ApprovalTwoId = WriterHandoffCanonical.uuid root "approvalTwoId"
            ValidUntil = WriterHandoffCanonical.time root "validUntil"
        }

    let private valid value =
        value.Epoch > 0L
        && value.OldGeneration > 0L
        && value.NewGeneration = value.OldGeneration + 1L
        && value.PrepareSequence > 0L
        && value.ApprovalOneId <> value.ApprovalTwoId

    let parse canonical =
        WriterHandoffCanonical.shape canonical fields "COMMIT"
        |> Option.bind (fun root ->
            try
                let value = decode root
                if valid value then Some value else None
            with _ ->
                None)
