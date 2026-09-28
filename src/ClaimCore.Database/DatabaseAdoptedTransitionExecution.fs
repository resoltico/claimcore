namespace ClaimCore.Database

open System.Security.Cryptography
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only signed adopted-copy changes preserve their true ProductExport/external genesis.
module internal DatabaseAdoptedTransitionExecution =
    let private outcome =
        function
        | AuthorityWriteOutcome.Applied _ -> AdministrationOutcome.Completed None
        | AuthorityWriteOutcome.Refused ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | AuthorityWriteOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private execute
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        verified
        canonical
        signature
        =
        if verified then
            match DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration() with
            | None -> AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
            | Some inventory ->
                use inventory = inventory

                ManagedCopyAdoptedTransitionAdministration.verifiedDeletion
                    connection
                    witness
                    (inventory :> ICopyAbsenceVerifier)
                    canonical
                    signature
                |> fun work -> work.GetAwaiter().GetResult()
                |> outcome
        else
            ManagedCopyAdoptedTransitionAdministration.transition
                connection
                witness
                canonical
                signature
            |> fun work -> work.GetAwaiter().GetResult()
            |> outcome

    let run connection witness verified canonicalPath signaturePath =
        match DatabaseCopyInputs.attestation canonicalPath with
        | Error reason -> Error reason
        | Ok canonical ->
            try
                match DatabaseCopyInputs.signature signaturePath with
                | Error reason -> Error reason
                | Ok signature ->
                    try
                        try
                            execute connection witness verified canonical signature |> Ok
                        with _ ->
                            AdministrationOutcome.CompletionUnknown
                                AdministrationFailure.CommitUnconfirmed
                            |> Ok
                    finally
                        CryptographicOperations.ZeroMemory(signature)
            finally
                CryptographicOperations.ZeroMemory(canonical)
