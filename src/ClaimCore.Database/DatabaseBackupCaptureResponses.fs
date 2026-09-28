namespace ClaimCore.Database

open System
open System.Buffers
open System.Globalization
open System.Text.Encodings.Web
open System.Text.Json

[<NoEquality; NoComparison>]
type internal BackupCaptureHeld =
    {
        Nonce: string
        LeaseId: Guid
        Cutoff: BackupCaptureCutoff
        PrimarySystemId: string
        PrimaryTimeline: int64
        WitnessSystemId: string
        WitnessTimeline: int64
        MaintenanceEvidenceSha256: string
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
        CycleRoot: string
    }

[<NoEquality; NoComparison>]
type internal BackupCaptureReceipt =
    {
        Nonce: string
        LeaseId: Guid
        CycleReceiptId: Guid
        WitnessSequence: int64
        WitnessHash: string
        ReceiptSha256: string
    }

/// Canonical ASCII owner responses accepted by the private Python orchestration.
module internal DatabaseBackupCaptureResponses =
    let private render write =
        let output = ArrayBufferWriter<byte>()

        use writer =
            new Utf8JsonWriter(
                output,
                JsonWriterOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
            )

        writer.WriteStartObject()
        write writer
        writer.WriteEndObject()
        writer.Flush()
        let bytes = Array.zeroCreate<byte>(output.WrittenCount + 1)
        output.WrittenSpan.CopyTo(bytes.AsSpan())
        bytes[bytes.Length - 1] <- byte '\n'

        match DatabaseRestoreCanonical.parse bytes with
        | None -> invalidOp "Private backup response is noncanonical."
        | Some document -> document.Dispose()

        bytes

    let private instant (value: DateTimeOffset) =
        if value.Offset <> TimeSpan.Zero then
            invalidOp "Private backup response time is not UTC."

        value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)

    let held (value: BackupCaptureHeld) =
        render (fun (writer: Utf8JsonWriter) ->
            writer.WriteString("checkedAt", instant value.CheckedAt)
            writer.WriteString("cutoffHash", Convert.ToHexStringLower value.Cutoff.WitnessHash)
            writer.WriteNumber("cutoffSequence", value.Cutoff.WitnessSequence)
            writer.WriteString("cycleRoot", value.CycleRoot)
            writer.WriteNumber("epoch", value.Cutoff.Epoch)
            writer.WriteString("format", "claimcore-backup-barrier-frame-1")
            writer.WriteString("installationId", value.Cutoff.InstallationId.ToString("D"))
            writer.WriteString("kind", "HELD")
            writer.WriteString("leaseId", value.LeaseId.ToString("D"))
            writer.WriteString("lineageId", value.Cutoff.LineageId.ToString("D"))
            writer.WriteString("maintenanceEvidenceSha256", value.MaintenanceEvidenceSha256)
            writer.WriteString("nonce", value.Nonce)
            writer.WriteString("primarySystemId", value.PrimarySystemId)
            writer.WriteNumber("primaryTimeline", value.PrimaryTimeline)
            writer.WriteString("validUntil", instant value.ValidUntil)
            writer.WriteString("witnessSystemId", value.WitnessSystemId)
            writer.WriteNumber("witnessTimeline", value.WitnessTimeline)
            writer.WriteNumber("writerGeneration", value.Cutoff.WriterGeneration))

    let receipt kind (value: BackupCaptureReceipt) =
        if kind <> "SEALED" && kind <> "OBSERVED" then
            invalidArg (nameof kind) "Backup receipt kind is invalid."

        render (fun (writer: Utf8JsonWriter) ->
            writer.WriteString("cycleReceiptId", value.CycleReceiptId.ToString("D"))
            writer.WriteString("format", "claimcore-backup-barrier-frame-1")
            writer.WriteString("kind", kind)
            writer.WriteString("leaseId", value.LeaseId.ToString("D"))
            writer.WriteString("nonce", value.Nonce)
            writer.WriteString("receiptSha256", value.ReceiptSha256)
            writer.WriteString("witnessHash", value.WitnessHash)
            writer.WriteNumber("witnessSequence", value.WitnessSequence))

    let aborted () =
        render (fun (writer: Utf8JsonWriter) ->
            writer.WriteString("format", "claimcore-backup-barrier-frame-1")
            writer.WriteString("kind", "ABORTED"))
