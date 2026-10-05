namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only terminal loss decision. Private evidence bytes never enter process arguments or diagnostics.
module internal DatabaseInstallationLossExecution =
    let private refused = DatabaseInputProblem.InstallationLossFileRefused

    let private required maximum path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 -> Ok bytes
        | _ -> Error refused

    let private knownOperations path =
        match PrivateFileService.readBinary 1048576 path with
        | Ok bytes -> Ok bytes
        | Error _ -> Error refused

    let private optional maximum path =
        if path = "MISSING" then
            Ok None
        else
            required maximum path |> Result.map Some

    let private clear (value: byte array option) =
        value |> Option.iter (fun bytes -> CryptographicOperations.ZeroMemory(bytes))

    let private withEvidence evidencePath checkpointPath knownPath action =
        optional 4194304 evidencePath
        |> Result.bind (fun evidence ->
            try
                optional 4194304 checkpointPath
                |> Result.bind (fun checkpoint ->
                    try
                        knownOperations knownPath
                        |> Result.bind (fun known ->
                            try
                                action evidence checkpoint known
                            finally
                                CryptographicOperations.ZeroMemory(known))
                    finally
                        clear checkpoint)
            finally
                clear evidence)

    let private witnessInputs ownerConnection =
        DatabaseWitnessInputs.witnessAuditConnection ()
        |> Result.bind (fun audit ->
            DatabaseWitnessInputs.witnessOwnerConnection ()
            |> Result.bind (fun witnessOwner ->
                try
                    let primary = OwnerConnection.builder ownerConnection
                    let reader = NpgsqlConnectionStringBuilder(audit)
                    let administrative = NpgsqlConnectionStringBuilder(witnessOwner)

                    if
                        reader.Username = "claimcore_witness_auditor"
                        && administrative.Username = "claimcore_witness_owner"
                        && reader.Host = administrative.Host
                        && reader.Port = administrative.Port
                        && reader.Database = administrative.Database
                        && not (
                            String.Equals(
                                primary.Host,
                                reader.Host,
                                StringComparison.OrdinalIgnoreCase
                            )
                            && primary.Port = reader.Port
                        )
                    then
                        Ok(audit, witnessOwner)
                    else
                        Error DatabaseInputProblem.WitnessFileInvalid
                with _ ->
                    Error DatabaseInputProblem.WitnessFileInvalid))

    let private withOwnerContext ownerConnection onException action =
        witnessInputs ownerConnection
        |> Result.bind (fun (audit, witnessOwner) ->
            DatabaseWitnessInputs.keyRing ()
            |> Result.bind (fun custody ->
                use custody = custody

                match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
                | null
                | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
                | path ->
                    try
                        use suppression = SuppressionKeyFile.Load(path)
                        let builder = OwnerConnection.builder ownerConnection
                        use owner = new NpgsqlConnection(builder.ConnectionString)
                        owner.Open()
                        OwnerConnection.requireIdentity owner
                        SchemaBaseline.requireCurrent owner
                        let identity, keyId, check = DatabaseVerifyData.identity owner

                        let commitments =
                            DatabaseVerifyData.commitments suppression identity keyId check

                        let store = Store.OpenAudit(audit, identity)
                        use witness = new WitnessProtocol(store, custody, identity)
                        action owner witnessOwner witness commitments
                    with _ ->
                        Ok onException))

    let private outcome =
        function
        | InstallationLossRetirementOutcome.Retired _ -> AdministrationOutcome.Completed None
        | InstallationLossRetirementOutcome.Refused ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | InstallationLossRetirementOutcome.AwaitingPrimary _
        | InstallationLossRetirementOutcome.AwaitingWitness _
        | InstallationLossRetirementOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private mode =
        function
        | "KNOWN_OPERATIONS" -> InstallationLossOperationSet.Known
        | "UNKNOWN_OPERATIONS" -> InstallationLossOperationSet.Unknown
        | _ -> invalidOp "Loss operation-set mode was not parsed."

    let draft
        ownerConnection
        firstKey
        secondKey
        evidencePath
        checkpointPath
        knownPath
        operationSet
        outputPath
        =
        withEvidence evidencePath checkpointPath knownPath (fun evidence checkpoint known ->
            withOwnerContext
                ownerConnection
                (AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed)
                (fun owner _ witness commitments ->
                    let candidate =
                        InstallationLossRetirementAdministration.draft
                            owner
                            witness
                            commitments
                            firstKey
                            secondKey
                            evidence
                            checkpoint
                            known
                            (mode operationSet)
                        |> fun work -> work.GetAwaiter().GetResult()

                    match candidate with
                    | None ->
                        Ok(
                            AdministrationOutcome.NotCommitted
                                AdministrationFailure.OperationFailed
                        )
                    | Some bytes ->
                        try
                            match PrivateFileService.writeNew 16384 outputPath bytes with
                            | Ok() -> Ok(AdministrationOutcome.Completed None)
                            | Error _ -> Error refused
                        finally
                            CryptographicOperations.ZeroMemory(bytes)))

    let private recordOrReconcile ownerConnection paths reconcile =
        withEvidence
            paths.EvidenceReport
            paths.IndependentCheckpoint
            paths.KnownOperations
            (fun evidence checkpoint known ->
                required 16384 paths.Candidate
                |> Result.bind (fun canonical ->
                    try
                        required 64 paths.SignatureOne
                        |> Result.bind (fun signatureOne ->
                            try
                                required 64 paths.SignatureTwo
                                |> Result.bind (fun signatureTwo ->
                                    try
                                        withOwnerContext
                                            ownerConnection
                                            (AdministrationOutcome.CompletionUnknown
                                                AdministrationFailure.CommitUnconfirmed)
                                            (fun owner witnessOwner witness commitments ->
                                                let run =
                                                    if reconcile then
                                                        InstallationLossRetirementAdministration
                                                            .reconcile
                                                    else
                                                        InstallationLossRetirementAdministration
                                                            .record

                                                run
                                                    owner
                                                    witnessOwner
                                                    witness
                                                    commitments
                                                    canonical
                                                    signatureOne
                                                    signatureTwo
                                                    known
                                                    evidence
                                                    checkpoint
                                                |> fun work -> work.GetAwaiter().GetResult()
                                                |> outcome
                                                |> Ok)
                                    finally
                                        CryptographicOperations.ZeroMemory(signatureTwo))
                            finally
                                CryptographicOperations.ZeroMemory(signatureOne))
                    finally
                        CryptographicOperations.ZeroMemory(canonical)))

    let record ownerConnection paths =
        recordOrReconcile ownerConnection paths false

    let reconcile ownerConnection paths =
        recordOrReconcile ownerConnection paths true
