namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Create-only owner-private review delivery. A partial temporary write is never the final
/// receipt path; retries preserve it and publish the same deterministic bytes after readback.
module internal DatabaseRealDataPlanReceipt =
    let private encode (planId: Guid) (activationId: Guid) (plan: BackupHealthActivationPlan) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WriteString("format", "claimcore-real-data-plan-review-1")
        writer.WriteString("planId", planId)
        writer.WriteString("activationId", activationId)
        writer.WriteString("planSha256", plan.PlanSha256)
        writer.WriteBase64String("canonicalPlan", ReadOnlySpan<byte>(plan.Canonical))
        writer.WriteEndObject()
        writer.Flush()
        let bytes = Array.append (stream.ToArray()) [| byte '\n' |]
        CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, int stream.Length))
        bytes

    let private target (path: string) =
        if
            not (Path.IsPathFullyQualified(path))
            || not (path.EndsWith(".json", StringComparison.Ordinal))
        then
            invalidOp "Private activation plan receipt path is invalid."

        let directory =
            Path.GetDirectoryName(path)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Private plan receipt directory is absent.")

        match PrivateFileService.requirePrivateDirectory directory with
        | Ok() -> directory
        | Error _ -> invalidOp "Private plan receipt directory is unsafe."

    let private same (path: string) (expected: byte array) =
        match PrivateFileService.readBinary 32768 path with
        | Ok actual ->
            try
                CryptographicOperations.FixedTimeEquals(actual.AsSpan(), expected.AsSpan())
            finally
                CryptographicOperations.ZeroMemory(actual)
        | Error _ -> false

    let publish (path: string) planId activationId plan =
        let directory = target path
        let expected = encode planId activationId plan

        try
            if File.Exists(path) then
                same path expected
            else
                let temporary =
                    Path.Combine(
                        directory,
                        ".claimcore-plan-" + Guid.NewGuid().ToString("N") + ".pending"
                    )

                match PrivateFileService.writeNew 32768 temporary expected with
                | Error _ -> false
                | Ok() ->
                    try
                        File.Move(temporary, path)
                        same path expected
                    with _ ->
                        same path expected
        finally
            CryptographicOperations.ZeroMemory(expected)
