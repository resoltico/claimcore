namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Fixed private inputs and outcome projection for owner-only W1 commands.
module internal DatabaseWriterHandoffPrivate =
    let privatePath name =
        match Environment.GetEnvironmentVariable(name) with
        | null
        | "" -> invalidOp "Owner-private W1 evidence setting is unavailable."
        | value -> value

    let binaryDigest () =
        let path = typeof<DatabaseCommand>.Assembly.Location

        if String.IsNullOrWhiteSpace path then
            invalidOp "Published Database binary is unavailable."

        use source = File.OpenRead(path)
        SHA256.HashData(source) |> Convert.ToHexStringLower

    let databaseNow (owner: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Owner database clock is unavailable."

    let reportFiles () =
        DatabaseRestoreReportInputs.load
            (privatePath "CLAIMCORE_WRITER_RESTORE_REPORT_FILE")
            (privatePath "CLAIMCORE_WRITER_RESTORE_SIGNATURE_FILE")
            (privatePath "CLAIMCORE_WRITER_RESTORE_INDEX_FILE")
        |> Result.defaultWith (fun _ -> invalidOp "Owner-private pre-W1 report is unavailable.")

    let fenceFiles () =
        let read maximum setting =
            match PrivateFileService.readBinary maximum (privatePath setting) with
            | Ok bytes when bytes.Length > 0 -> bytes
            | _ -> invalidOp "Owner-private old-writer fence is unavailable."

        let body = read 32768 "CLAIMCORE_WRITER_FENCE_FILE"

        try
            let signature = read 64 "CLAIMCORE_WRITER_FENCE_SIGNATURE_FILE"

            if signature.Length <> 64 then
                CryptographicOperations.ZeroMemory(signature)
                invalidOp "Owner-private old-writer fence signature is invalid."

            body, signature
        with _ ->
            CryptographicOperations.ZeroMemory(body)
            reraise ()

    let publication () =
        DatabaseRestorePublication.current ()
        |> Option.defaultWith (fun () -> invalidOp "Reviewed publication root is unavailable.")

    let scope () =
        // A signed fence alone cannot substitute for code-owned independent-host proof.
        invalidOp "Independent-host W1 publication and probe authority is unavailable."

    let outcome =
        function
        | WriterHandoffOwnerOutcome.Prepared _
        | WriterHandoffOwnerOutcome.Settled _ -> AdministrationOutcome.Completed None
        | WriterHandoffOwnerOutcome.Refused ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | WriterHandoffOwnerOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
