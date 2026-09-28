namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only read and create-new private candidate; distinct humans sign it elsewhere.
module internal DatabaseWriterHandoffAbortDraftExecution =
    let private derive
        ownerConnection
        app
        audit
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        handoffId
        firstKey
        secondKey
        =
        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        OwnerConnection.requireIdentity owner
        SchemaBaseline.requireCurrent owner
        let identity, keyId, check = DatabaseVerifyData.identity owner
        let commitments = DatabaseVerifyData.commitments suppression identity keyId check
        use source = RuntimeDataSource.create app
        let store = Store.OpenAudit(audit, identity)
        use witness = new WitnessProtocol(store, custody, identity)
        witness.AdmitReadOnly()

        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Private writer capability is unavailable.")

        capability.Use(fun material ->
            WriterHandoffOwnerAbortDraft.create
                owner
                source
                witness
                (Some commitments)
                handoffId
                firstKey
                secondKey
                material
            |> fun task -> task.GetAwaiter().GetResult())

    let private withSuppression owner app audit custody handoffId firstKey secondKey =
        match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
        | path ->
            try
                use suppression = SuppressionKeyFile.Load(path)
                derive owner app audit custody suppression handoffId firstKey secondKey |> Ok
            with _ ->
                Ok None

    let run ownerConnection handoffId firstKey secondKey outputPath =
        DatabaseWriterHandoffAbortExecution.sourceInput ownerConnection
        |> Result.bind (fun (app, audit, _) ->
            DatabaseWitnessInputs.keyRing ()
            |> Result.bind (fun custody ->
                use custody = custody
                withSuppression ownerConnection app audit custody handoffId firstKey secondKey))
        |> Result.bind (function
            | None -> Ok(AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed)
            | Some candidate ->
                try
                    match PrivateFileService.writeNew 16384 outputPath candidate with
                    | Ok() -> Ok(AdministrationOutcome.Completed None)
                    | Error _ -> Error DatabaseInputProblem.WriterHandoffFileRefused
                finally
                    CryptographicOperations.ZeroMemory(candidate))
