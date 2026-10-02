namespace ClaimCore.Database

open System
open System.Buffers
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

[<RequireQualifiedAccess>]
type internal FencedTailOwnerRefusal =
    | TrustAnchorUnavailable
    | PrivateEvidenceRefused
    | IndependentHostUnavailable
    | EvidenceRecheckFailed

/// Owner-only final-tail recheck and activation. The normal path can succeed only when the
/// source-pinned independent-host issuer returns a fresh six-host typed proof.
module internal DatabaseFencedTailOwnerExecution =
    let private privatePath name =
        match Environment.GetEnvironmentVariable(name) with
        | null
        | "" -> invalidOp "Owner-private fenced-tail setting is unavailable."
        | value -> value

    let private databaseNow (owner: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Owner database clock is unavailable."

    let private rechecked
        ownerConnection
        (loaded: LoadedFencedTail)
        (publication: TrustedRestorePublication)
        (host: VerifiedIndependentHostProof)
        now
        =
        let witnessAudit =
            DatabaseWitnessInputs.witnessAuditConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness audit connection is unavailable.")

        let witnessOwner =
            DatabaseWitnessInputs.witnessOwnerConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness owner connection is unavailable.")

        use custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness key custody is unavailable.")

        use suppression =
            SuppressionKeyFile.Load(privatePath "CLAIMCORE_SUPPRESSION_KEY_FILE")

        let local =
            DatabaseFencedTailQualification.verify
                publication
                host.Scope
                ownerConnection
                witnessAudit
                witnessOwner
                custody
                suppression
                loaded
                host.ExpectedProbeSha256
                host.ProbeEvidenceSha256
                (privatePath "CLAIMCORE_RESTORE_ARCHIVE_ROOT")
                now

        if DatabaseFencedTailHostBinding.matchesLocal host local now then
            Ok(local, publication)
        else
            Error FencedTailOwnerRefusal.EvidenceRecheckFailed

    let private qualified ownerConnection (paths: FencedTailPaths) =
        if DatabaseRestorePublication.current () |> Option.isNone then
            Error FencedTailOwnerRefusal.TrustAnchorUnavailable
        else
            let loaded =
                try
                    Some(DatabaseFencedTailInputs.load paths)
                with _ ->
                    None

            match loaded with
            | None -> Error FencedTailOwnerRefusal.PrivateEvidenceRefused
            | Some loaded ->
                try
                    try
                        let builder = OwnerConnection.builder ownerConnection
                        use owner = new NpgsqlConnection(builder.ConnectionString)
                        owner.Open()
                        let now = databaseNow owner

                        let publication =
                            DatabaseRestorePublication.currentAt now
                            |> Option.defaultWith (fun () ->
                                invalidOp "Reviewed publication is unavailable at DB clock.")

                        match
                            DatabaseIndependentHostProof.current
                                owner
                                loaded.Evidence
                                publication
                                now
                        with
                        | None -> Error FencedTailOwnerRefusal.IndependentHostUnavailable
                        | Some host when host.Scope <> "full" || not host.RealDataReady ->
                            Error FencedTailOwnerRefusal.IndependentHostUnavailable
                        | Some host -> rechecked ownerConnection loaded publication host now
                    with _ ->
                        Error FencedTailOwnerRefusal.EvidenceRecheckFailed
                finally
                    DatabaseFencedTailInputs.dispose loaded

    let private refusal =
        function
        | FencedTailOwnerRefusal.TrustAnchorUnavailable -> "TRUST_ANCHOR_UNAVAILABLE"
        | FencedTailOwnerRefusal.PrivateEvidenceRefused -> "PRIVATE_EVIDENCE_REFUSED"
        | FencedTailOwnerRefusal.IndependentHostUnavailable -> "INDEPENDENT_HOST_UNAVAILABLE"
        | FencedTailOwnerRefusal.EvidenceRecheckFailed -> "EVIDENCE_RECHECK_FAILED"

    let private encode (nonce: string) outcome =
        let output = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(output)
        writer.WriteStartObject()
        writer.WriteString("format", "claimcore-fenced-tail-recheck-1")
        writer.WriteString("nonce", nonce)

        match outcome with
        | Error reason ->
            writer.WriteString("status", "REFUSED")
            writer.WriteString("reason", refusal reason)
            writer.WriteBoolean("realDataReady", false)
        | Ok(proof: FencedTailVerification, _) ->
            writer.WriteString("status", "EVIDENCE_RECHECKED")
            writer.WriteString("reportSha256", proof.ReportSha256)
            writer.WriteString("fenceReportSha256", proof.FenceReportSha256)
            writer.WriteString("supplementSha256", proof.SupplementSha256)
            writer.WriteString("finalWalObjectSha256", proof.FinalWalObjectSha256)
            writer.WriteNumber("finalWalObjects", proof.FinalWalObjects)
            writer.WriteNumber("w1Sequence", proof.W1Sequence)
            writer.WriteString("w1Hash", proof.W1Hash)
            writer.WriteNumber("writerGeneration", proof.WriterGeneration)
            // Success is returned only after fresh independent and local evidence match.
            writer.WriteBoolean("realDataReady", true)

        writer.WriteEndObject()
        writer.Flush()
        let bytes = Array.zeroCreate<byte>(output.WrittenCount + 1)
        output.WrittenSpan.CopyTo(bytes.AsSpan())
        bytes[bytes.Length - 1] <- byte '\n'
        bytes

    let verify owner paths nonce (output: Stream) (errors: Stream) =
        let outcome = qualified owner paths
        let target = if Result.isOk outcome then output else errors

        try
            let bytes = encode nonce outcome
            target.Write(bytes, 0, bytes.Length)
            target.Flush()
            if Result.isOk outcome then 0 else 3
        with _ ->
            3

    let private activationVerifier owner publication =
        function
        | None ->
            { new IWriterActivationEvidenceVerifier with
                member _.Verify(_, _) =
                    Task.FromResult<WriterActivationQualification option>(None)
            }
        | Some proof -> DatabaseFencedTailHostBinding.verifier owner publication proof

    let private commitActivation owner source witnessOwner witness verifier commitments candidate =
        try
            match
                WriterHandoffActivation.activate
                    owner
                    source
                    witnessOwner
                    witness
                    verifier
                    (Some commitments)
                    candidate
                    CancellationToken.None
                |> fun pending -> pending.GetAwaiter().GetResult()
            with
            | WriterActivationOutcome.Activated _ -> AdministrationOutcome.Completed None
            | WriterActivationOutcome.Refused ->
                AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
            | WriterActivationOutcome.Unconfirmed _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        with _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private activated
        ownerConnection
        publication
        (candidate: WriterActivationEvidence)
        (freshProof: FencedTailVerification option)
        =
        let app, _, witnessOwner =
            DatabaseWriterHandoffAbortExecution.sourceInput ownerConnection
            |> Result.defaultWith (fun _ -> invalidOp "Owner W2 connections are unavailable.")

        let witnessWriter =
            DatabaseWitnessInputs.witnessWriterConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness writer connection is unavailable.")

        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        let identity, keyId, check = DatabaseVerifyData.identity owner

        use suppression =
            SuppressionKeyFile.Load(privatePath "CLAIMCORE_SUPPRESSION_KEY_FILE")

        let commitments = DatabaseVerifyData.commitments suppression identity keyId check
        use source = RuntimeDataSource.create app

        let custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness key custody is unavailable.")

        use capability =
            WriterCapabilityFile.Load(privatePath "CLAIMCORE_NEW_WRITER_CAPABILITY_FILE")

        capability.Use(fun newBytes ->
            use witness =
                new WitnessProtocol(
                    new Store(witnessWriter, identity, newBytes),
                    custody,
                    identity
                )

            let verifier = activationVerifier owner publication freshProof
            commitActivation owner source witnessOwner witness verifier commitments candidate)

    let activate owner paths =
        let fresh () =
            match qualified owner paths with
            | Error _ -> Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
            | Ok(proof, publication) ->
                try
                    let candidate = DatabaseFencedTailEvidenceMapping.activation proof
                    Ok(activated owner publication candidate (Some proof))
                with _ ->
                    Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)

        match DatabaseFencedTailHistoricalOwner.tryCandidate owner paths with
        | None -> fresh ()
        | Some(candidate, publication) ->
            try
                match activated owner publication candidate None with
                | AdministrationOutcome.NotCommitted _ -> fresh ()
                | result -> Ok result
            with _ ->
                fresh ()
