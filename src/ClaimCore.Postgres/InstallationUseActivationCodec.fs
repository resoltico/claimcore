namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Text
open System.Text.Json

[<NoEquality; NoComparison>]
type internal InstallationUseActivationRecord =
    {
        EventId: Guid
        PlanId: Guid
        PlanSha256: byte array
        HealthCertificateSha256: byte array
        PolicySha256: byte array
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        ExpectedWitnessSequence: int64
        ExpectedWitnessHash: byte array
        KnownCopyInventorySha256: byte array
        HealthCheckedAt: DateTimeOffset
        HealthValidUntil: DateTimeOffset
        Approvals: InstallationUseApprovalPair
    }

/// Strict readback of an already witnessed owner decision; this never qualifies a new health
/// certificate or turns a missing primary row into permission to initiate activation.
module internal InstallationUseActivationCodec =
    let private names =
        [
            "version"
            "kind"
            "eventId"
            "planId"
            "installationId"
            "lineageId"
            "epoch"
            "writerGeneration"
            "dataUseScope"
            "fromPhase"
            "toPhase"
            "activationPlanSha256"
            "policySha256"
            "healthCertificateSha256"
            "expectedWitnessSequence"
            "expectedWitnessHash"
            "knownCopyInventorySha256"
            "healthSignerKeyId"
            "healthSignerHolderActorId"
            "healthCheckedAt"
            "healthValidUntil"
            "reviewWitnessSequence"
            "reviewWitnessHash"
            "approvalOneId"
            "approvalOneActorId"
            "approvalOneGrantRevision"
            "approvalOneIntentSequence"
            "approvalOneIntentHash"
            "approvalOneSettlementSequence"
            "approvalOneSettlementHash"
            "approvalOneExpiresAt"
            "approvalTwoId"
            "approvalTwoActorId"
            "approvalTwoGrantRevision"
            "approvalTwoIntentSequence"
            "approvalTwoIntentHash"
            "approvalTwoSettlementSequence"
            "approvalTwoSettlementHash"
            "approvalTwoExpiresAt"
        ]

    let private text (name: string) (root: JsonElement) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Activation evidence text is absent.")

    let private digest name root =
        let raw = text name root
        let bytes = Convert.FromHexString(raw)

        if bytes.Length <> 32 || Convert.ToHexStringLower(bytes) <> raw then
            invalidOp "Activation evidence digest is invalid."

        bytes

    let private exactId name root =
        let raw = text name root
        let parsed = Guid.ParseExact(raw, "D")

        if parsed = Guid.Empty || parsed.ToString("D") <> raw then
            invalidOp "Activation evidence identity is invalid."

        parsed

    let private instant name root =
        let raw = text name root

        let parsed =
            DateTimeOffset.ParseExact(
                raw,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            )

        if parsed.Offset <> TimeSpan.Zero || parsed.ToUniversalTime().ToString("O") <> raw then
            invalidOp "Activation evidence time is invalid."

        parsed

    let private approval prefix (root: JsonElement) =
        {
            ApprovalId = exactId (prefix + "Id") root
            ActorId = exactId (prefix + "ActorId") root
            GrantRevision = root.GetProperty(prefix + "GrantRevision").GetInt64()
            IntentSequence = root.GetProperty(prefix + "IntentSequence").GetInt64()
            IntentHash = digest (prefix + "IntentHash") root
            SettlementSequence = root.GetProperty(prefix + "SettlementSequence").GetInt64()
            SettlementHash = digest (prefix + "SettlementHash") root
            ExpiresAt = instant (prefix + "ExpiresAt") root
        }

    let private shape (root: JsonElement) =
        (root.EnumerateObject() |> Seq.map _.Name |> Seq.toList) = names
        && root.GetProperty("version").GetInt32() = 1
        && text "kind" root = "ACTIVATE_REAL_DATA"
        && text "dataUseScope" root = "REAL_DATA"
        && text "fromPhase" root = "BOOTSTRAP_NO_CASES"
        && text "toPhase" root = "ACTIVE"

    let private projection (root: JsonElement) =
        let record: InstallationUseActivationRecord =
            {
                EventId = exactId "eventId" root
                PlanId = exactId "planId" root
                PlanSha256 = digest "activationPlanSha256" root
                HealthCertificateSha256 = digest "healthCertificateSha256" root
                PolicySha256 = digest "policySha256" root
                InstallationId = exactId "installationId" root
                LineageId = exactId "lineageId" root
                Epoch = root.GetProperty("epoch").GetInt64()
                WriterGeneration = root.GetProperty("writerGeneration").GetInt64()
                ExpectedWitnessSequence = root.GetProperty("expectedWitnessSequence").GetInt64()
                ExpectedWitnessHash = digest "expectedWitnessHash" root
                KnownCopyInventorySha256 = digest "knownCopyInventorySha256" root
                HealthCheckedAt = instant "healthCheckedAt" root
                HealthValidUntil = instant "healthValidUntil" root
                Approvals =
                    {
                        First = approval "approvalOne" root
                        Second = approval "approvalTwo" root
                        ReviewSequence = root.GetProperty("reviewWitnessSequence").GetInt64()
                        ReviewHash = digest "reviewWitnessHash" root
                    }
            }

        record

    let decode (canonical: byte array) =
        try
            if isNull (box canonical) || canonical.Length < 2 || canonical.Length > 8192 then
                None
            else
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement
                let exact = Encoding.UTF8.GetBytes(root.GetRawText())

                if exact <> canonical || not (shape root) then
                    None
                else
                    let record = projection root
                    let one = record.Approvals.First
                    let two = record.Approvals.Second

                    if
                        record.EventId
                        <> InstallationUseActivationCandidate.eventIdFromPlan
                            record.InstallationId
                            record.PlanSha256
                        || record.PlanId
                           <> InstallationUseActivationCandidate.planIdFromDigest
                               record.InstallationId
                               record.PlanSha256
                        || one.ActorId = two.ActorId
                        || one.SettlementSequence >= two.IntentSequence
                        || two.SettlementSequence > record.ExpectedWitnessSequence
                        || record.HealthValidUntil <= record.HealthCheckedAt
                    then
                        None
                    else
                        Some record
        with _ ->
            None
