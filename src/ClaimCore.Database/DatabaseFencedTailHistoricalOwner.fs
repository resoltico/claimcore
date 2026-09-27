namespace ClaimCore.Database

open System
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// Only reconstructs the originally signed candidate. It never grants fresh W2 authority;
/// the Postgres historical branch must find and reconcile the exact prior witness W2 ticket.
module internal DatabaseFencedTailHistoricalOwner =
    let private databaseNow (owner: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Owner database clock is unavailable."

    let private matching (host: HistoricalIndependentHostDigest) (local: FencedTailVerification) =
        host.Scope = "full"
        && host.InstallationId = local.InstallationId
        && host.LineageId = local.LineageId
        && host.Epoch = local.Epoch
        && host.WriterGeneration = local.WriterGeneration
        && host.W1Sequence = local.W1Sequence
        && host.W1Hash = local.W1Hash
        && host.PublicationManifestSha256 = local.PublicationManifestSha256
        && host.ExpectedProbeSha256 = local.IndependentProbeSha256
        && host.ProbeEvidenceSha256 = local.ProbeEvidenceSha256
        && host.AggregateSha256 = local.ProbeEvidenceSha256

    let private verifyLocal
        ownerConnection
        (owner: NpgsqlConnection)
        publication
        (loaded: LoadedFencedTail)
        (host: HistoricalIndependentHostDigest)
        =
        let witnessAudit =
            DatabaseWitnessInputs.witnessAuditConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Historical witness audit is unavailable.")

        let archiveRoot =
            Environment.GetEnvironmentVariable("CLAIMCORE_RESTORE_ARCHIVE_ROOT")
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Historical WAL archive setting is unavailable.")

        let identity, _, _ = DatabaseVerifyData.identity owner

        let custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ -> invalidOp "Historical witness custody is unavailable.")

        use witness =
            new WitnessProtocol(Store.OpenAudit(witnessAudit, identity), custody, identity)

        DatabaseFencedTailHistorical.verified
            ownerConnection
            witness
            publication
            loaded
            host.ProbeEvidenceSha256
            archiveRoot

    let tryCandidate ownerConnection (paths: FencedTailPaths) =
        if DatabaseRestorePublication.reviewedRootKey () |> Option.isNone then
            None
        else
            try
                let loaded = DatabaseFencedTailInputs.load paths

                try
                    let atIssuance =
                        DatabaseFencedTailHistorical.issuedAt loaded.Evidence.Supplement

                    match DatabaseRestorePublication.currentAt atIssuance with
                    | None -> None
                    | Some publication ->
                        let builder = OwnerConnection.builder ownerConnection
                        use owner = new NpgsqlConnection(builder.ConnectionString)
                        owner.Open()

                        match
                            DatabaseIndependentHostProof.historicalDigest
                                owner
                                loaded.Evidence
                                publication
                                (databaseNow owner)
                        with
                        | None -> None
                        | Some host ->
                            let local = verifyLocal ownerConnection owner publication loaded host

                            if matching host local then
                                Some(
                                    DatabaseFencedTailEvidenceMapping.activation local,
                                    publication
                                )
                            else
                                None
                finally
                    DatabaseFencedTailInputs.dispose loaded
            with _ ->
                None
