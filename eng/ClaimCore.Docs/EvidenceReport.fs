namespace ClaimCore.Docs

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module EvidenceReport =
    let private boundedMessage (root: RepositoryRoot) (message: string) =
        let sanitized =
            message.Replace(root.Path, "<repository>", StringComparison.Ordinal)
            |> Seq.map (fun value -> if Char.IsControl(value) then ' ' else value)
            |> Array.ofSeq
            |> String

        if sanitized.Length <= 1024 then
            sanitized
        else
            sanitized.Substring(0, 1024)

    let private stage (writer: Utf8JsonWriter) (value: StageManifest) =
        writer.WriteStartObject()
        writer.WriteString("stageId", value.StageId)
        writer.WriteString("outcome", value.Outcome)
        writer.WriteString("treeSha256", value.Output.TreeSha256)
        writer.WriteEndObject()

    let private report (writer: Utf8JsonWriter) (value: TestReport) =
        writer.WriteStartObject()
        writer.WriteString("assembly", value.Assembly)
        writer.WriteString("testRunId", value.RunId)
        writer.WriteNumber("total", value.Total)
        writer.WriteNumber("passed", value.Passed)
        writer.WriteEndObject()

    let private contract (writer: Utf8JsonWriter) (value: ContractEvidence) =
        writer.WriteStartObject()
        writer.WriteString("id", value.Id)
        writer.WriteStartArray("passedTests")

        value.PassedTests
        |> List.iter (fun name -> writer.WriteStringValue(name: string))

        writer.WriteEndArray()
        writer.WriteEndObject()

    let private source (writer: Utf8JsonWriter) (value: SourceIdentity option) =
        writer.WriteStartObject("source")

        match value with
        | Some identity ->
            writer.WriteString("contentSha256", identity.ContentSha256)
            writer.WriteString("locksSha256", identity.LocksSha256)
            writer.WriteString("state", identity.State)
        | None ->
            writer.WriteNull("contentSha256")
            writer.WriteNull("locksSha256")
            writer.WriteString("state", "unavailable")

        writer.WriteEndObject()

    let private assurance
        (writer: Utf8JsonWriter)
        (synchronized: string)
        (behavior: string)
        (semantic: string)
        (reviewerKinds: string list)
        (notRun: string list)
        =
        writer.WriteStartObject("assurance")
        writer.WriteString("synchronized", synchronized)
        writer.WriteString("behaviorExercised", behavior)
        writer.WriteString("semanticReview", semantic)
        writer.WriteStartArray("semanticReviewerKinds")

        reviewerKinds
        |> List.distinct
        |> List.sort
        |> List.iter (fun value -> writer.WriteStringValue(value: string))

        writer.WriteEndArray()
        writer.WriteStartArray("notRun")
        notRun |> List.sort |> List.iter (writer.WriteStringValue: string -> unit)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private header (writer: Utf8JsonWriter) (runId: string) (attempt: int) (outcome: string) =
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteString("runId", runId)
        writer.WriteNumber("attempt", attempt)
        writer.WriteString("outcome", outcome)

    let write
        (root: RepositoryRoot)
        (runId: string)
        (attempt: int)
        (identity: SourceIdentity)
        (result: EvidenceResult)
        =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        header writer runId attempt "passed"
        source writer (Some identity)

        assurance
            writer
            "passed"
            "passed"
            "repository-attested"
            (result.Reviews |> List.map _.ReviewerKind)
            []

        writer.WriteStartArray("stages")
        result.Stages |> List.sortBy _.StageId |> List.iter (stage writer)
        writer.WriteEndArray()
        writer.WriteStartArray("reports")
        result.Reports |> List.sortBy _.Assembly |> List.iter (report writer)
        writer.WriteEndArray()
        writer.WriteStartArray("contractEvidence")
        result.Contracts |> List.sortBy _.Id |> List.iter (contract writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        let bytes = stream.ToArray() |> Array.append [| byte '\n' |]

        AtomicFile.write root $"artifacts/evidence/{runId}/{attempt}/evidence.json" bytes
        |> Result.map ignore

    let writeFailure
        (root: RepositoryRoot)
        (runId: string)
        (attempt: int)
        (identity: SourceIdentity option)
        (message: string)
        =
        let absent =
            Stages.definitions
            |> List.map _.Id
            |> List.filter (fun stage ->
                let relative = EvidenceReconciliation.stageManifestPath runId attempt stage

                match Repository.registeredPath root relative with
                | Ok path -> not (File.Exists(path))
                | Error _ -> true)

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        header writer runId attempt "failed"
        source writer identity
        assurance writer "not-complete" "not-complete" "not-assessed" [] absent
        writer.WriteStartArray("stages")
        writer.WriteEndArray()
        writer.WriteStartArray("reports")
        writer.WriteEndArray()
        writer.WriteStartArray("contractEvidence")
        writer.WriteEndArray()
        writer.WriteStartArray("errors")
        writer.WriteStringValue(boundedMessage root message)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()

        AtomicFile.write
            root
            $"artifacts/evidence/{runId}/{attempt}/evidence.json"
            (stream.ToArray() |> Array.append [| byte '\n' |])
        |> Result.map ignore
