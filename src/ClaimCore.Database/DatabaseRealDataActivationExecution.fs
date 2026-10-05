namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only activation and exact repair; neither path accepts a caller-supplied trust flag.
module internal DatabaseRealDataActivationExecution =
    let private separate ownerConnection writer witnessOwner =
        let primary = OwnerConnection.builder ownerConnection
        let writerRole = OwnerConnection.builder writer
        let ownerRole = OwnerConnection.builder witnessOwner

        writerRole.Username = "claimcore_witness_writer"
        && ownerRole.Username = "claimcore_witness_owner"
        && not (
            String.Equals(primary.Host, writerRole.Host, StringComparison.OrdinalIgnoreCase)
            && primary.Port = writerRole.Port
        )
        && String.Equals(writerRole.Host, ownerRole.Host, StringComparison.OrdinalIgnoreCase)
        && writerRole.Port = ownerRole.Port

    let private primaryIdentity ownerConnection =
        let builder = OwnerConnection.builder ownerConnection
        let owner = new NpgsqlConnection(builder.ConnectionString)

        try
            owner.Open()
            OwnerConnection.requireIdentity owner
            SchemaBaseline.requireCurrent owner

            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    owner
                )

            use reader = command.ExecuteReader()

            if not (reader.Read()) then
                invalidOp "Installation identity is absent."

            let identity: Identity =
                {
                    InstallationId = reader.GetGuid(0)
                    LineageId = reader.GetGuid(1)
                    Epoch = reader.GetInt64(2)
                }

            if reader.Read() then
                invalidOp "Installation identity is ambiguous."

            owner, identity
        with _ ->
            owner.Dispose()
            reraise ()

    let private witnessFor ownerConnection =
        let writer =
            DatabaseWitnessInputs.witnessWriterConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness writer is unavailable.")

        let witnessOwner =
            DatabaseWitnessInputs.witnessOwnerConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness owner is unavailable.")

        if not (separate ownerConnection writer witnessOwner) then
            invalidOp "Independent witness administration input is invalid."

        let custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness key custody is unavailable.")

        let owner, identity = primaryIdentity ownerConnection

        try
            let capability =
                DatabaseWitnessInputs.writerCapability ()
                |> Result.defaultWith (fun _ -> invalidOp "Witness capability is unavailable.")

            let store = capability.Use(fun material -> new Store(writer, identity, material))
            owner, witnessOwner, custody, capability, new WitnessProtocol(store, custody, identity)
        with _ ->
            owner.Dispose()
            custody.Dispose()
            reraise ()

    let private auditWitnessFor ownerConnection =
        let audit =
            DatabaseWitnessInputs.witnessAuditConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Witness auditor is unavailable.")

        let primary = OwnerConnection.builder ownerConnection
        let auditor = OwnerConnection.builder audit

        if
            auditor.Username <> "claimcore_witness_auditor"
            || (String.Equals(primary.Host, auditor.Host, StringComparison.OrdinalIgnoreCase)
                && primary.Port = auditor.Port)
        then
            invalidOp "Independent witness audit input is invalid."

        let owner, identity = primaryIdentity ownerConnection

        try
            let custody =
                DatabaseWitnessInputs.keyRing ()
                |> Result.defaultWith (fun _ -> invalidOp "Witness key custody is unavailable.")

            try
                let store = Store.OpenAudit(audit, identity)
                owner, custody, new WitnessProtocol(store, custody, identity)
            with _ ->
                custody.Dispose()
                reraise ()
        with _ ->
            owner.Dispose()
            reraise ()

    let private outcome =
        function
        | InstallationUseActivationOutcome.Activated _ -> AdministrationOutcome.Completed None
        | InstallationUseActivationOutcome.Refused ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | InstallationUseActivationOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private invoke ownerConnection firstApproval secondApproval proof plan =
        try
            let owner, witnessOwner, custody, capability, witness = witnessFor ownerConnection
            use owner = owner
            use custody = custody
            use capability = capability
            use witness = witness

            InstallationUseActivationOwner.activate
                owner
                witnessOwner
                witness
                proof
                plan
                firstApproval
                secondApproval
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> outcome
            |> Ok
        with _ ->
            Ok(AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed)

    let private publishedDigest ownerConnection planId =
        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        OwnerConnection.requireIdentity owner
        SchemaBaseline.requireCurrent owner

        use command =
            new NpgsqlCommand(
                "SELECT plan_sha256 FROM claimcore.installation_data_use_plans WHERE plan_id=@plan",
                owner
            )

        Sql.uuid command "plan" planId

        match command.ExecuteScalar() with
        | :? (byte array) as digest when digest.Length = 32 -> Some(Convert.ToHexStringLower digest)
        | _ -> None

    let activate
        ownerConnection
        policy
        originalEvidence
        freshEvidence
        planId
        firstApproval
        secondApproval
        =
        let expected =
            try
                publishedDigest ownerConnection planId
            with _ ->
                None

        match expected with
        | None -> Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
        | Some expectedPlanSha ->
            match
                DatabaseBackupHealthExecution.qualifyForActivation
                    ownerConnection
                    policy
                    originalEvidence
                    freshEvidence
                    expectedPlanSha
                |> fun work -> work.GetAwaiter().GetResult()
            with
            | Error reason -> Error reason
            | Ok None -> Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
            | Ok(Some(proof, plan)) ->
                try
                    if
                        InstallationUseActivationCandidate.planIdFromDigest
                            plan.InstallationId
                            (Convert.FromHexString plan.PlanSha256)
                        <> planId
                    then
                        Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
                    else
                        invoke ownerConnection firstApproval secondApproval proof plan
                finally
                    CryptographicOperations.ZeroMemory(proof.PolicyCanonical)
                    CryptographicOperations.ZeroMemory(proof.Canonical)
                    CryptographicOperations.ZeroMemory(plan.Canonical)

    let reconcile ownerConnection =
        try
            let owner, custody, witness = auditWitnessFor ownerConnection
            use owner = owner
            use custody = custody
            use witness = witness

            InstallationUseActivationReconcile.run owner witness CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
            |> outcome
            |> Ok
        with _ ->
            Ok(AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed)
