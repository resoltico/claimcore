namespace ClaimCore.Database

open System.Security.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness

/// Only an operator-reviewed build may initialize the real-data witness scope.
module internal DatabaseInstallationScope =
    let reviewedForScope scope =
        if scope = InstallationUseScope.SyntheticOnly then
            true
        else
            match ReviewedDeploymentRoot.current () with
            | None -> false
            | Some profile ->
                CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
                true
