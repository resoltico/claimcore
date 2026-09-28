namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.HostSecurity

/// Bounded owner-private inputs; partial reads are zeroed before a refusal.
module internal DatabaseManagedCopyInventoryInputs =
    let load () =
        match
            Environment.GetEnvironmentVariable("CLAIMCORE_COPY_LOCATION_REGISTRY_FILE"),
            Environment.GetEnvironmentVariable("CLAIMCORE_COPY_LOCATION_INSPECTION_FILE"),
            Environment.GetEnvironmentVariable("CLAIMCORE_COPY_COMMITMENT_KEY_FILE")
        with
        | null, _, _
        | _, null, _
        | _, _, null
        | "", _, _
        | _, "", _
        | _, _, "" -> None
        | registryPath, inspectionPath, keyPath ->
            match PrivateFileService.readBinary (8 * 1024 * 1024) registryPath with
            | Error _ -> None
            | Ok registry ->
                try
                    match PrivateFileService.readBinary (8 * 1024 * 1024) inspectionPath with
                    | Error _ ->
                        CryptographicOperations.ZeroMemory(registry)
                        None
                    | Ok inspection ->
                        try
                            let key = ManagedCopyCommitmentKey.Load(keyPath)
                            Some(registry, inspection, key)
                        with _ ->
                            CryptographicOperations.ZeroMemory(inspection)
                            CryptographicOperations.ZeroMemory(registry)
                            None
                with _ ->
                    CryptographicOperations.ZeroMemory(registry)
                    None
