namespace ClaimCore.Database

open System
open System.Globalization
open System.Text.Json

module internal DatabaseRestoreReportClaims =
    let private expected =
        [
            "format"
            "source"
            "scope"
            "realDataReady"
            "cycleId"
            "backupCaptureSequence"
            "backupCaptureHash"
            "installationId"
            "lineageId"
            "epoch"
            "witnessCutoff"
            "witnessCutoffHash"
            "primarySystemId"
            "primaryTimeline"
            "witnessSystemId"
            "witnessTimeline"
            "primaryRegisteredWalHorizon"
            "witnessRegisteredWalHorizon"
            "reportSignerKeyId"
            "verifierBinarySha256"
            "evidenceIndexSha256"
            "checkpointSha256"
            "signedInventoryFileSha256"
            "quiescentBarrierSha256"
            "catalogManifestSha256"
            "authorityRevision"
            "checkedAt"
            "validUntil"
            "recoveredDataChecked"
            "pairCompared"
            "catalogVerified"
            "dataAuditVerified"
            "registeredWalVerified"
            "recoveryTailUnsealed"
            "authorityReconciled"
            "managedCopiesRegistered"
            "newerFencesApplied"
            "quiescentAuditBarrierVerified"
            "oidcIssuerHttpsVerified"
            "twoOwnerRosterVerified"
            "pendingIntents"
            "custodyObjects"
            "custodyKeyId"
            "custodyPublicKeySha256"
            "authorizedApprovers"
        ]

    let private uuid name root =
        let raw =
            DatabaseRestoreCanonical.text name root
            |> Option.ofObj
            |> Option.defaultValue ""

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Restore report UUID is invalid."

    let private sha name root =
        let raw =
            DatabaseRestoreCanonical.text name root
            |> Option.ofObj
            |> Option.defaultValue ""

        if
            raw.Length <> 64
            || not (
                raw
                |> Seq.forall (fun character ->
                    ('0' <= character && character <= '9')
                    || ('a' <= character && character <= 'f'))
            )
        then
            invalidOp "Restore report digest is invalid."

        raw

    let private systemId name root =
        let raw =
            DatabaseRestoreCanonical.text name root
            |> Option.ofObj
            |> Option.defaultValue ""

        if raw.Length < 1 || raw.Length > 20 || not (raw |> Seq.forall Char.IsAsciiDigit) then
            invalidOp "Restore report system identity is invalid."

        raw

    let private instant name root =
        let raw =
            DatabaseRestoreCanonical.text name root
            |> Option.ofObj
            |> Option.defaultValue ""

        let mutable parsed = DateTimeOffset.MinValue

        if
            not (
                DateTimeOffset.TryParseExact(
                    raw,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    &parsed
                )
            )
            || parsed.Offset <> TimeSpan.Zero
            || parsed.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw
        then
            invalidOp "Restore report instant is invalid."

        parsed

    let private requireTrue root =
        [
            "recoveredDataChecked"
            "pairCompared"
            "catalogVerified"
            "dataAuditVerified"
            "registeredWalVerified"
            "recoveryTailUnsealed"
            "authorityReconciled"
            "managedCopiesRegistered"
            "newerFencesApplied"
            "quiescentAuditBarrierVerified"
            "twoOwnerRosterVerified"
        ]
        |> List.iter (fun name ->
            if not (DatabaseRestoreCanonical.flag name root) then
                invalidOp "Restore report check is incomplete.")

    let private custody (root: JsonElement) =
        let objects = root.GetProperty("custodyObjects")

        if not (DatabaseRestoreCanonical.exactProperties [ "archive"; "checkpoint" ] objects) then
            invalidOp "Restore report custody inventory is incomplete."

        let parse (name: string) =
            let item = objects.GetProperty(name)

            if
                not (
                    DatabaseRestoreCanonical.exactProperties [ "objectId"; "sha256"; "bytes" ] item
                )
                || DatabaseRestoreCanonical.number "bytes" item < 1L
            then
                invalidOp "Restore report custody object is invalid."

            {
                ObjectId = uuid "objectId" item
                Sha256 = sha "sha256" item
                Bytes = DatabaseRestoreCanonical.number "bytes" item
            }

        parse "archive", parse "checkpoint"

    let private qualificationScope root =
        let scope =
            DatabaseRestoreCanonical.text "scope" root
            |> Option.ofObj
            |> Option.defaultValue ""

        if
            not (DatabaseRestoreCanonical.exactProperties expected root)
            || DatabaseRestoreCanonical.text "format" root
               <> "claimcore-restore-qualification-1"
            || DatabaseRestoreCanonical.text "source" root <> "ClaimCore.Database"
            || (scope <> "full" && scope <> "synthetic-only")
        then
            invalidOp "Restore report format is unsupported."

        let oidcHttps = DatabaseRestoreCanonical.flag "oidcIssuerHttpsVerified" root
        let realDataReady = DatabaseRestoreCanonical.flag "realDataReady" root

        if
            realDataReady
            || (scope = "full" && not oidcHttps)
            || (scope = "synthetic-only" && oidcHttps)
        then
            invalidOp "Restore report qualification scope is inconsistent."

        scope, realDataReady

    let private checkedInterval root now =
        let checkedAt = instant "checkedAt" root
        let validUntil = instant "validUntil" root

        if
            checkedAt > now
            || validUntil <= now
            || now - checkedAt > TimeSpan.FromHours(1.)
            || validUntil - checkedAt > TimeSpan.FromHours(1.)
        then
            invalidOp "Restore report has expired."

        checkedAt, validUntil

    let private parsed root now =
        let scope, realDataReady = qualificationScope root
        requireTrue root

        let archiveCustody, checkpointCustody = custody root
        let custodyKeyId = uuid "custodyKeyId" root
        let custodyPublicKeySha = sha "custodyPublicKeySha256" root

        let authorityRevision = DatabaseRestoreCanonical.number "authorityRevision" root
        let authorizedApprovers = DatabaseRestoreOwnerClaims.parse root authorityRevision

        if DatabaseRestoreCanonical.number "pendingIntents" root <> 0L then
            invalidOp "Restore report has unresolved witness intent."

        let checkedAt, validUntil = checkedInterval root now

        {
            Scope = scope
            RealDataReady = realDataReady
            InstallationId = uuid "installationId" root
            LineageId = uuid "lineageId" root
            Epoch = DatabaseRestoreCanonical.number "epoch" root
            CycleId = uuid "cycleId" root
            BackupCaptureSequence = DatabaseRestoreCanonical.number "backupCaptureSequence" root
            BackupCaptureHash = sha "backupCaptureHash" root
            WitnessCutoff = DatabaseRestoreCanonical.number "witnessCutoff" root
            WitnessCutoffHash = sha "witnessCutoffHash" root
            PrimarySystemId = systemId "primarySystemId" root
            PrimaryTimeline = DatabaseRestoreCanonical.number "primaryTimeline" root
            WitnessSystemId = systemId "witnessSystemId" root
            WitnessTimeline = DatabaseRestoreCanonical.number "witnessTimeline" root
            PrimaryRegisteredWalHorizon =
                DatabaseRestoreCanonical.text "primaryRegisteredWalHorizon" root
                |> Option.ofObj
                |> Option.defaultValue ""
            WitnessRegisteredWalHorizon =
                DatabaseRestoreCanonical.text "witnessRegisteredWalHorizon" root
                |> Option.ofObj
                |> Option.defaultValue ""
            SignerKeyId = uuid "reportSignerKeyId" root
            VerifierBinarySha256 = sha "verifierBinarySha256" root
            EvidenceIndexSha256 = sha "evidenceIndexSha256" root
            CheckpointSha256 = sha "checkpointSha256" root
            SignedInventoryFileSha256 = sha "signedInventoryFileSha256" root
            QuiescentBarrierSha256 = sha "quiescentBarrierSha256" root
            CatalogManifestSha256 = sha "catalogManifestSha256" root
            AuthorityRevision = authorityRevision
            AuthorizedApprovers = authorizedApprovers
            ArchiveCustody = archiveCustody
            CheckpointCustody = checkpointCustody
            CustodyKeyId = custodyKeyId
            CustodyPublicKeySha256 = custodyPublicKeySha
            CheckedAt = checkedAt
            ValidUntil = validUntil
        }

    let parse (bytes: byte array) now =
        match DatabaseRestoreCanonical.parse bytes with
        | None -> None
        | Some document ->
            use document = document

            try
                let claim = parsed document.RootElement now

                if
                    claim.Epoch > 0L
                    && claim.WitnessCutoff > claim.BackupCaptureSequence
                    && claim.BackupCaptureSequence >= 0L
                    && claim.PrimaryTimeline > 0L
                    && claim.WitnessTimeline > 0L
                    && claim.PrimarySystemId <> claim.WitnessSystemId
                    && claim.AuthorityRevision > 0L
                then
                    Some claim
                else
                    None
            with _ ->
                None
