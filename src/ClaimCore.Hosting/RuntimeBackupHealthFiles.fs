namespace ClaimCore.Hosting

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Real-data deployment profiles re-open every short-lived signed certificate through
/// private nofollow handles. Generic source-preview builds have no reviewed profile.
module internal RuntimeBackupHealthFiles =
    let private required name =
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp "Private backup health setting is absent.")

    let private read maximum path =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes when bytes.Length > 0 -> bytes
        | _ -> invalidOp "Private backup health evidence is unavailable."

    let private withFiles
        (verify: ReviewedDeploymentProfile -> byte array -> byte array -> byte array -> Task<unit>)
        =
        task {
            let profile =
                ReviewedDeploymentRoot.current ()
                |> Option.defaultWith (fun () ->
                    invalidOp "Real-data backup health has no reviewed deployment root.")

            try
                let policy = read 65536 (required "CLAIMCORE_BACKUP_HEALTH_POLICY_FILE")

                try
                    let certificatePath = required "CLAIMCORE_BACKUP_HEALTH_CERTIFICATE_FILE"
                    let certificate = read 65536 certificatePath

                    try
                        let signature = read 64 (certificatePath + ".sig")

                        try
                            if signature.Length <> 64 then
                                invalidOp "Private backup health signature is invalid."

                            do! verify profile policy certificate signature
                        finally
                            CryptographicOperations.ZeroMemory(signature)
                    finally
                        CryptographicOperations.ZeroMemory(certificate)
                finally
                    CryptographicOperations.ZeroMemory(policy)
            finally
                CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
        }

    let require
        (resources: RuntimeResources)
        (state: InstallationUseState)
        (ct: CancellationToken)
        =
        task {
            match state.Scope with
            | InstallationUseScope.SyntheticOnly -> ()
            | InstallationUseScope.RealData ->
                do!
                    withFiles (fun profile policy certificate signature ->
                        task {
                            use! connection = resources.DataSource.OpenConnectionAsync(ct)

                            do!
                                BackupHealthRuntimeAdmission.verify
                                    connection
                                    resources.Witness
                                    profile
                                    policy
                                    certificate
                                    signature
                                    ct
                        })
        }

    let requireLocked
        (resources: RuntimeResources)
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (ct: CancellationToken)
        =
        withFiles (fun profile policy certificate signature ->
            BackupHealthRuntimeAdmission.verifyLocked
                connection
                transaction
                resources.Witness
                profile
                policy
                certificate
                signature
                ct)
