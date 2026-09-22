module ClaimCore.DocsTests.StageCollectionTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private run = "collection-test"
let private digest = String.replicate 64 "a"

let private manifest stageId =
    let definition = Stages.tryFind stageId |> Option.get

    {
        SchemaVersion = 2
        StageId = stageId
        RunId = run
        Attempt = 1
        Outcome = "success"
        StartedUtc = DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero)
        FinishedUtc = DateTimeOffset(2026, 9, 22, 0, 0, 1, TimeSpan.Zero)
        Platform = "linux"
        Procedure = definition.Procedure
        RequiredOutputs = []
        OutputRoot = "artifacts/output"
        Output =
            {
                SchemaVersion = 1
                StageId = stageId
                SourceSha256 = digest
                LocksSha256 = digest
                TreeSha256 = PublishManifest.treeSha256 []
                Files = []
                Toolchain =
                    {
                        DotnetSdk = "10.0.401"
                        Node = Some "26.9.0"
                        Npm = Some "11.19.1"
                        PostgreSql = None
                        OperatingSystem = "synthetic"
                        Architecture = "x64"
                    }
            }
    }

let private add (repository: TempRepository) producer fileName value =
    repository.WriteBytes(
        $"artifacts/evidence-producers/claimcore-stage-{producer}-{run}-1/{fileName}",
        StageManifestFormat.serialize value
    )

let private destination id =
    EvidenceReconciliation.stageManifestPath run 1 id

let private accepted () =
    use repository = new TempRepository()
    let value = manifest "fantomas"
    add repository "quality" "fantomas.json" value |> ignore
    StageCollection.collect repository.Root run 1 |> requireOk
    let bytes = repository.Read(destination "fantomas") |> Encoding.UTF8.GetBytes
    Expect.equal (StageManifestFormat.parse bytes |> requireOk) value "Producer bytes preserved"

let private rejected label producer name alter =
    testCase label (fun () ->
        use repository = new TempRepository()
        add repository "quality" "fantomas.json" (manifest "fantomas") |> ignore
        add repository producer name (alter (manifest "fsharplint")) |> ignore
        StageCollection.collect repository.Root run 1 |> requireError |> ignore

        Expect.isFalse
            (File.Exists(Path.Combine(repository.Path, destination "fantomas")))
            "All records are validated before any destination is written")

let private noOverwrite () =
    use repository = new TempRepository()
    add repository "quality" "fantomas.json" (manifest "fantomas") |> ignore
    repository.Write(destination "fantomas", "existing") |> ignore
    StageCollection.collect repository.Root run 1 |> requireError |> ignore
    Expect.equal (repository.Read(destination "fantomas")) "existing" "Never overwrite evidence"

let private symlink () =
    use repository = new TempRepository()
    let target = add repository "quality" "fantomas.json" (manifest "fantomas")

    File.CreateSymbolicLink(
        Path.Combine(Path.GetDirectoryName(target) |> Option.ofObj |> Option.get, "link.json"),
        target
    )
    |> ignore

    StageCollection.collect repository.Root run 1 |> requireError |> ignore

let private singleOwner () =
    let ids = Stages.definitions |> List.map _.Id
    Expect.equal ids.Length (Set.ofList ids).Count "Each stage is declared once"

    Expect.isTrue
        (Stages.definitions
         |> List.forall (fun stage -> not (String.IsNullOrWhiteSpace stage.Producer)))
        "Every declared stage has one producer"

    Expect.equal
        (Stages.tryFind "dependency-security" |> Option.get).Producer
        "quality"
        "One security owner"

    Expect.isNone
        (Stages.tryFind "dependency-currency")
        "Freshness is independent of PR correctness"

let tests =
    testList
        "isolated producer collection"
        [
            testCase "retains exact manifests from their registered producer" accepted
            testCase "every stage has a unique identity and declared owner" singleOwner
            rejected
                "rejects a stage from the wrong producer before copying"
                "frontend"
                "fsharplint.json"
                id
            rejected
                "rejects duplicate stage data before copying any record"
                "frontend"
                "fantomas.json"
                (fun _ -> manifest "fantomas")
            rejected "rejects a stale run identity" "quality" "fsharplint.json" (fun value ->
                { value with RunId = "other-run" })
            rejected "rejects a stale attempt identity" "quality" "fsharplint.json" (fun value ->
                { value with Attempt = 2 })
            rejected "rejects unregistered stages" "quality" "unknown.json" (fun value ->
                { value with StageId = "unknown" })
            rejected
                "rejects downloaded reconciliation-owned evidence"
                "evidence"
                "evidence-inputs.json"
                (fun _ -> manifest "evidence-inputs")
            testCase "refuses to overwrite an existing destination" noOverwrite
            testCase "refuses symlinked producer evidence" symlink
        ]
