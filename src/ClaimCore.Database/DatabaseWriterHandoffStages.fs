namespace ClaimCore.Database

open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal WriterHandoffExecutionInputs =
    {
        OwnerConnection: string
        App: string
        WitnessAudit: string
        WitnessOwner: string
        Custody: IKeyCustody
        Suppression: SuppressionKeyFile
        Files: RestoreReportFiles
        FenceBody: byte array
        FenceSignature: byte array
        Canonical: byte array
        Signature: byte array
        OldCapability: WriterCapabilityFile
        NewCapability: WriterCapabilityFile
    }

/// Owner W1 stage dispatch keeps private proof reading separate from witnessed execution.
module internal DatabaseWriterHandoffStages =
    let private withCapabilities (inputs: WriterHandoffExecutionInputs) action =
        inputs.OldCapability.Use(fun oldBytes ->
            inputs.NewCapability.Use(fun newBytes -> action oldBytes newBytes))

    let private preparedVerifier
        (inputs: WriterHandoffExecutionInputs)
        (owner: NpgsqlConnection)
        (proposal: WriterHandoffPreparation)
        =
        let now = DatabaseWriterHandoffPrivate.databaseNow owner
        let mode = DatabaseWriterHandoffPrivate.scope ()
        let trusted = DatabaseWriterHandoffPrivate.publication ()

        DatabaseWriterHandoffQualification.preparation
            trusted
            mode
            inputs.OwnerConnection
            inputs.WitnessAudit
            inputs.WitnessOwner
            inputs.Custody
            inputs.Suppression
            inputs.Files
            proposal
            inputs.Canonical
            inputs.FenceBody
            inputs.FenceSignature
            (DatabaseWriterHandoffPrivate.binaryDigest ())
            now

    let private executePrepared
        (inputs: WriterHandoffExecutionInputs)
        (owner: NpgsqlConnection)
        source
        identity
        commitments
        verifier
        witnessWriter
        oldBytes
        newBytes
        =
        let store = new Store(witnessWriter, identity, oldBytes)
        use witness = new WitnessProtocol(store, inputs.Custody, identity)

        try
            WriterHandoffOwnerPreparation.prepare
                owner
                source
                inputs.WitnessOwner
                witness
                verifier
                (Some commitments)
                inputs.Canonical
                inputs.Signature
                oldBytes
                newBytes
            |> fun task -> task.GetAwaiter().GetResult()
            |> DatabaseWriterHandoffPrivate.outcome
        with _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let prepared (inputs: WriterHandoffExecutionInputs) witnessWriter proposal =
        let builder = OwnerConnection.builder inputs.OwnerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        let verifier = preparedVerifier inputs owner proposal
        let identity, keyId, check = DatabaseVerifyData.identity owner

        let commitments =
            DatabaseVerifyData.commitments inputs.Suppression identity keyId check

        use source = RuntimeDataSource.create inputs.App

        withCapabilities inputs (fun oldBytes newBytes ->
            executePrepared
                inputs
                owner
                source
                identity
                commitments
                verifier
                witnessWriter
                oldBytes
                newBytes)

    let private readPrepared (owner: NpgsqlConnection) (witness: WitnessProtocol) handoffId =
        use transaction = owner.BeginTransaction()

        let prepared =
            WriterHandoffOwnerRead.preparation owner transaction witness handoffId
            |> Option.defaultWith (fun () -> invalidOp "Exact pending W1 preparation is absent.")

        transaction.Rollback()
        prepared

    let private settledVerifier
        (inputs: WriterHandoffExecutionInputs)
        (owner: NpgsqlConnection)
        (prepared: PrimaryWriterPreparation)
        =
        let now = DatabaseWriterHandoffPrivate.databaseNow owner
        let mode = DatabaseWriterHandoffPrivate.scope ()
        let trusted = DatabaseWriterHandoffPrivate.publication ()

        DatabaseWriterHandoffQualification.settlement
            trusted
            mode
            inputs.OwnerConnection
            inputs.WitnessAudit
            inputs.WitnessOwner
            inputs.Custody
            inputs.Suppression
            inputs.Files
            prepared
            inputs.Canonical
            inputs.FenceBody
            inputs.FenceSignature
            (DatabaseWriterHandoffPrivate.binaryDigest ())
            now

    let private executeSettled
        (inputs: WriterHandoffExecutionInputs)
        (owner: NpgsqlConnection)
        source
        (witness: WitnessProtocol)
        commitments
        verifier
        oldBytes
        newBytes
        =
        try
            WriterHandoffOwnerSettlement.commit
                owner
                source
                inputs.WitnessOwner
                witness
                verifier
                (Some commitments)
                inputs.Canonical
                inputs.Signature
                oldBytes
                newBytes
            |> fun task -> task.GetAwaiter().GetResult()
            |> DatabaseWriterHandoffPrivate.outcome
        with _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let settled (inputs: WriterHandoffExecutionInputs) (value: WriterHandoffSettlement) =
        let builder = OwnerConnection.builder inputs.OwnerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        let identity, keyId, check = DatabaseVerifyData.identity owner

        let commitments =
            DatabaseVerifyData.commitments inputs.Suppression identity keyId check

        use witness =
            new WitnessProtocol(
                Store.OpenAudit(inputs.WitnessAudit, identity),
                inputs.Custody,
                identity
            )

        witness.AdmitReadOnly()
        let prepared = readPrepared owner witness value.HandoffId
        let verifier = settledVerifier inputs owner prepared
        use source = RuntimeDataSource.create inputs.App

        withCapabilities inputs (fun oldBytes newBytes ->
            executeSettled inputs owner source witness commitments verifier oldBytes newBytes)
