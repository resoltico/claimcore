namespace ClaimCore.Docs

open System
open System.IO
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module EvidenceReconciliation =
    let private contractTag =
        Regex("(?:^|\\.)\\[(CC-[A-Z]{2,12}-[0-9]{3})\\] ", RegexOptions.CultureInvariant)

    let private tagStart = Regex("(?:^|\\.)\\[CC-", RegexOptions.CultureInvariant)

    let contracts (declarations: ContractDeclaration list) (reports: TestReport list) =
        let declared = declarations |> List.map _.Id |> Set.ofList
        let tagged = ResizeArray<string * string>()
        let errors = ResizeArray<string>()

        for report in reports do
            for test in report.Tests do
                let matches = contractTag.Matches(test.Name)

                if tagStart.IsMatch(test.Name) && matches.Count = 0 then
                    errors.Add($"Malformed contract tag in test '{test.Name}'.")
                elif matches.Count > 1 then
                    errors.Add($"Test '{test.Name}' carries more than one contract tag.")
                elif matches.Count = 1 then
                    let id = matches[0].Groups[1].Value

                    if declared.Contains(id) then
                        tagged.Add(id, test.Name)
                    else
                        errors.Add($"Test names unknown contract '{id}'.")

        let evidence =
            declarations
            |> List.map (fun contract ->
                {
                    Id = contract.Id
                    PassedTests =
                        tagged
                        |> Seq.filter (fst >> (=) contract.Id)
                        |> Seq.map snd
                        |> Seq.sort
                        |> Seq.toList
                })

        for contract in evidence do
            if contract.PassedTests.IsEmpty then
                errors.Add($"Contract '{contract.Id}' has no executed passing evidence.")

        if errors.Count = 0 then
            Ok evidence
        else
            Error(List.ofSeq errors)

    let stageManifestPath (runId: string) attempt (stageId: string) =
        $"artifacts/evidence/{runId}/{attempt}/stages/{stageId}.json"

    let loadStage (root: RepositoryRoot) (runId: string) attempt (definition: StageDefinition) =
        let relative = stageManifestPath runId attempt definition.Id

        match Repository.registeredPath root relative with
        | Error message -> Error message
        | Ok path ->
            match Repository.ensureExistingSafe root path with
            | Error message -> Error message
            | Ok safe ->
                StageManifestFormat.parse (File.ReadAllBytes(safe))
                |> Result.bind (fun manifest ->
                    StageManifestFormat.validate definition runId attempt manifest
                    |> Result.map (fun () -> manifest))

    let trxPath (root: RepositoryRoot) (manifest: StageManifest) (report: TestReportDefinition) =
        match Repository.registeredPath root manifest.OutputRoot with
        | Error message -> Error message
        | Ok outputRoot ->
            let expected = report.StageId + "/" + report.FileName

            let matches =
                manifest.Output.Files |> List.filter (fun file -> file.Path = expected)

            match matches with
            | [ file ] -> Repository.ensureExistingSafe root (Path.Combine(outputRoot, file.Path))
            | _ ->
                Error
                    $"Stage '{manifest.StageId}' does not contain exactly one '{report.FileName}'."
