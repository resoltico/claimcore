namespace ClaimCore.Docs

open System
open System.Runtime.InteropServices
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module Stages =
    let definitions = StageCatalog.definitions

    // Counts are deliberately centralized here. A changed discovery count requires a reviewed edit.
    let private report stageId assembly fileName =
        let names = TestInventory.names assembly

        {
            StageId = stageId
            Assembly = assembly
            FileName = fileName
            ExpectedTests = names.Count
            ExpectedNames = names
        }

    let private platformReports prefix assembly fileName =
        [
            report (prefix + "-linux") assembly fileName
            report (prefix + "-macos") assembly fileName
            report (prefix + "-windows") assembly fileName
        ]

    let testReports =
        platformReports "unit" "ClaimCore.Tests" "ClaimCore.Tests.trx"
        @ platformReports
            "architecture"
            "ClaimCore.ArchitectureTests"
            "ClaimCore.ArchitectureTests.trx"
        @ platformReports
            "fuzz"
            "ClaimCore.FuzzQualificationTests"
            "ClaimCore.FuzzQualificationTests.trx"
        @ platformReports "web" "ClaimCore.WebTests" "ClaimCore.WebTests.trx"
        @ platformReports "docs" "ClaimCore.DocsTests" "ClaimCore.DocsTests.trx"
        @ [
            report "integration-linux" "ClaimCore.IntegrationTests" "ClaimCore.IntegrationTests.trx"
            report
                "recovery-qualification"
                "ClaimCore.RecoveryQualificationTests"
                "ClaimCore.RecoveryQualificationTests.trx"
            report
                "concurrency-qualification"
                "ClaimCore.ConcurrencyQualificationTests"
                "ClaimCore.ConcurrencyQualificationTests.trx"
            report
                "fresh-baseline-qualification"
                "ClaimCore.MigrationQualificationTests"
                "ClaimCore.MigrationQualificationTests.trx"
            report "acceptance-linux" "ClaimCore.AcceptanceTests" "ClaimCore.AcceptanceTests.trx"
        ]


    let tryFind (id: string) =
        definitions |> List.tryFind (fun item -> item.Id = id)

    let currentPlatform () =
        if OperatingSystem.IsWindows() then "windows"
        elif OperatingSystem.IsMacOS() then "macos"
        elif OperatingSystem.IsLinux() then "linux"
        else RuntimeInformation.OSDescription.ToLowerInvariant()

    let validRunId (value: string) =
        Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)

    let displayRequirement requirement =
        match requirement with
        | OutputRequirement.Exact path -> "exact:" + path
        | OutputRequirement.Suffix suffix -> "suffix:" + suffix
        | OutputRequirement.Prefix prefix -> "prefix:" + prefix
        | OutputRequirement.NativeHostSecurityLibrary -> "native:claimcore-hostsecurity"

    let satisfies (platform: string) (files: PublishFile list) requirement =
        match requirement with
        | OutputRequirement.Exact expected ->
            files |> List.exists (fun file -> file.Path = expected)
        | OutputRequirement.Suffix suffix ->
            files
            |> List.exists (fun file -> file.Path.EndsWith(suffix, StringComparison.Ordinal))
        | OutputRequirement.Prefix prefix ->
            files
            |> List.exists (fun file -> file.Path.StartsWith(prefix, StringComparison.Ordinal))
        | OutputRequirement.NativeHostSecurityLibrary ->
            let hasExact name =
                files |> List.exists (fun file -> file.Path = name)

            match platform with
            | "macos" -> hasExact "libclaimcore_hostsecurity_native.dylib"
            | "linux" -> hasExact "libclaimcore_hostsecurity_native.so"
            | "windows" ->
                files
                |> List.forall (fun file ->
                    not (
                        file.Path.StartsWith(
                            "libclaimcore_hostsecurity_native.",
                            StringComparison.Ordinal
                        )
                    ))
            | _ -> false
