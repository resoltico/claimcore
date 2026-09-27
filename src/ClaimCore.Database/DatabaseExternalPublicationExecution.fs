namespace ClaimCore.Database

open System
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

module internal DatabaseExternalPublicationExecution =
    let private outcome =
        function
        | ExternalCopyPublicationOutcome.Published _ -> AdministrationOutcome.Completed None
        | ExternalCopyPublicationOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        | ExternalCopyPublicationOutcome.ResourceUnavailable
        | ExternalCopyPublicationOutcome.PrivateLocationUnknown
        | ExternalCopyPublicationOutcome.AuditUnavailable _ ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed

    let private execute
        ownerConnection
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        suppression
        submission
        =
        let admitted =
            try
                let identity, keyId, check = DatabaseVerifyData.identity connection
                Some(DatabaseVerifyData.commitments suppression identity keyId check)
            with _ ->
                None

        match admitted with
        | None -> AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed |> Ok
        | Some commitments ->
            try

                let location =
                    DatabaseExternalPublicationLocation() :> IExternalCopyPublicationPrivateLocation

                ManagedCopyExternalPublicationOwner.publish
                    ownerConnection
                    witness
                    commitments
                    location
                    submission
                    Threading.CancellationToken.None
                |> fun work -> work.GetAwaiter().GetResult()
                |> outcome
                |> Ok
            with _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
                |> Ok

    let private withKey ownerConnection connection witness submission =
        match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
        | path ->
            let key =
                try
                    Some(SuppressionKeyFile.Load(path))
                with _ ->
                    None

            match key with
            | None -> Error DatabaseInputProblem.SuppressionKeyFileRefused
            | Some suppression ->
                use suppression = suppression
                execute ownerConnection connection witness suppression submission

    let run ownerConnection connection witness proposalPath =
        DatabaseExternalPublicationInputs.proposal proposalPath
        |> Result.bind (fun submission ->
            try
                withKey ownerConnection connection witness submission
            finally
                DatabaseExternalPublicationInputs.zero submission)
