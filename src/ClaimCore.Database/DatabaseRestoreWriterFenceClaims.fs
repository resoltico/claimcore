namespace ClaimCore.Database

open System
open System.Globalization

[<NoEquality; NoComparison>]
type internal WriterFenceClaims =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        OldGeneration: int64
        NewGeneration: int64
        ReportSha256: string
        IndependentProbeSha256: string
        OldEndpointId: Guid
        OldEndpointAddressSha256: string
        OldPrimaryRoleOid: int64
        OldWitnessRoleOid: int64
        OldPrimaryCredentialSha256: string
        OldWitnessCredentialSha256: string
        PrimarySessionSetSha256: string
        WitnessSessionSetSha256: string
        CheckpointSignerKeyId: Guid
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

/// An independently signed old-host attestation; a signature alone does not prove remote isolation.
module internal DatabaseRestoreWriterFenceClaims =
    let private fields =
        [
            "format"
            "installationId"
            "lineageId"
            "epoch"
            "oldGeneration"
            "newGeneration"
            "reportSha256"
            "independentProbeSha256"
            "checkpointSignerKeyId"
            "oldEndpointId"
            "oldEndpointAddressSha256"
            "oldPrimaryRoleOid"
            "oldWitnessRoleOid"
            "oldPrimaryCredentialSha256"
            "oldWitnessCredentialSha256"
            "primarySessionSetSha256"
            "witnessSessionSetSha256"
            "oldWriterStopped"
            "primarySessionsTerminated"
            "witnessSessionsTerminated"
            "oldEndpointIsolated"
            "primaryCredentialRevoked"
            "witnessCredentialRevoked"
            "preIsolationCommitReconciled"
            "checkedAt"
            "validUntil"
        ]

    let private text name root =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Writer fence text is unavailable.")

    let private uuid name root =
        let raw = text name root

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Writer fence identity is invalid."

    let private digest name root =
        let raw = text name root

        if
            raw.Length <> 64
            || raw
               |> Seq.exists (fun character ->
                   not (
                       ('0' <= character && character <= '9')
                       || ('a' <= character && character <= 'f')
                   ))
        then
            invalidOp "Writer fence digest is invalid."

        raw

    let private instant name root =
        let raw = text name root
        let mutable value = DateTimeOffset.MinValue

        if
            not (
                DateTimeOffset.TryParseExact(
                    raw,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    &value
                )
            )
            || value.Offset <> TimeSpan.Zero
            || value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw
        then
            invalidOp "Writer fence time is invalid."

        value

    let private requireClaims root =
        for name in
            [
                "oldWriterStopped"
                "primarySessionsTerminated"
                "witnessSessionsTerminated"
                "oldEndpointIsolated"
                "primaryCredentialRevoked"
                "witnessCredentialRevoked"
                "preIsolationCommitReconciled"
            ] do
            if not (DatabaseRestoreCanonical.flag name root) then
                invalidOp "Writer fence claim is incomplete."

    let private requireAuthority
        checkedAt
        validUntil
        epoch
        oldGeneration
        newGeneration
        primaryRole
        witnessRole
        now
        =
        if
            checkedAt > now
            || validUntil <= now
            || validUntil - checkedAt > TimeSpan.FromMinutes(15.)
            || epoch < 1L
            || oldGeneration < 1L
            || newGeneration <> oldGeneration + 1L
            || primaryRole < 1L
            || primaryRole > int64 UInt32.MaxValue
            || witnessRole < 1L
            || witnessRole > int64 UInt32.MaxValue
        then
            invalidOp "Writer fence authority or expiry is invalid."

    let private decode root now =
        if
            not (DatabaseRestoreCanonical.exactProperties fields root)
            || text "format" root <> "claimcore-old-writer-isolation-1"
        then
            invalidOp "Writer fence format is invalid."

        requireClaims root

        let checkedAt = instant "checkedAt" root
        let validUntil = instant "validUntil" root
        let epoch = DatabaseRestoreCanonical.number "epoch" root
        let oldGeneration = DatabaseRestoreCanonical.number "oldGeneration" root
        let newGeneration = DatabaseRestoreCanonical.number "newGeneration" root
        let primaryRole = DatabaseRestoreCanonical.number "oldPrimaryRoleOid" root
        let witnessRole = DatabaseRestoreCanonical.number "oldWitnessRoleOid" root

        requireAuthority
            checkedAt
            validUntil
            epoch
            oldGeneration
            newGeneration
            primaryRole
            witnessRole
            now

        {
            InstallationId = uuid "installationId" root
            LineageId = uuid "lineageId" root
            Epoch = epoch
            OldGeneration = oldGeneration
            NewGeneration = newGeneration
            ReportSha256 = digest "reportSha256" root
            IndependentProbeSha256 = digest "independentProbeSha256" root
            OldEndpointId = uuid "oldEndpointId" root
            OldEndpointAddressSha256 = digest "oldEndpointAddressSha256" root
            OldPrimaryRoleOid = primaryRole
            OldWitnessRoleOid = witnessRole
            OldPrimaryCredentialSha256 = digest "oldPrimaryCredentialSha256" root
            OldWitnessCredentialSha256 = digest "oldWitnessCredentialSha256" root
            PrimarySessionSetSha256 = digest "primarySessionSetSha256" root
            WitnessSessionSetSha256 = digest "witnessSessionSetSha256" root
            CheckpointSignerKeyId = uuid "checkpointSignerKeyId" root
            CheckedAt = checkedAt
            ValidUntil = validUntil
        }

    let parse (bytes: byte array) now =
        if bytes.Length < 2 || bytes.Length > 32768 then
            None
        else
            match DatabaseRestoreCanonical.parse bytes with
            | None -> None
            | Some document ->
                use document = document

                try
                    Some(decode document.RootElement now)
                with _ ->
                    None
