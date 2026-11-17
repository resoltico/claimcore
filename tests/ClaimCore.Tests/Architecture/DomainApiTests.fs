module ClaimCore.Tests.Architecture.DomainApiTests

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open Expecto
open ClaimCore.Domain

let private compile directory name source =
    let path = Path.Combine(directory, name + ".fsx")

    File.WriteAllText(
        path,
        "#r "
        + JsonSerializer.Serialize(typeof<Registration>.Assembly.Location)
        + "\nopen ClaimCore.Domain\n"
        + source
        + "\n"
    )

    let start =
        ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in [ "fsi"; "--nologo"; "--exec"; path ] do
        start.ArgumentList.Add argument

    let result = ClaimCore.TestSupport.BoundedProcess.run start None 32768 30000
    result.ExitCode, System.Text.Encoding.UTF8.GetString result.StandardError

[<Tests>]
let tests =
    testCase
        "typed business APIs reject cross-group and date-as-amount access at compilation"
        (fun () ->
            let owned = Directory.CreateTempSubdirectory "claimcore-typed-api-"

            try
                let code, errors =
                    compile
                        owned.FullName
                        "valid"
                        "let date (record: Registration) = Registration.incidentDate record\nlet money (record: Registration) = Registration.claimed record |> Money.amount |> Amount.value"

                Expect.equal code 0 ("Valid typed positive control: " + errors)

                let code, errors =
                    compile
                        owned.FullName
                        "cross-group"
                        "let wrong (record: Registration) = Registration.value BusinessField.PaymentDate record"

                Expect.isTrue
                    (code <> 0 && errors.Contains("FS1093", StringComparison.Ordinal))
                    "The private registration representation cannot expose an arbitrary-field accessor"

                let code, errors =
                    compile
                        owned.FullName
                        "wrong-scalar"
                        "let wrong (record: Registration) = Registration.incidentDate record |> Amount.value"

                Expect.isTrue
                    (code <> 0 && errors.Contains("FS0001", StringComparison.Ordinal))
                    "A calendar date cannot enter an amount accessor"
            finally
                owned.Delete true)


[<Tests>]
let selectedFactsBoundary =
    testCase "selected decision facts cannot enter complete replay APIs" (fun () ->
        let owned = Directory.CreateTempSubdirectory "claimcore-family-api-"

        try
            let code, errors =
                compile
                    owned.FullName
                    "valid-families"
                    "let complete (family: Aggregates.CaseFamily) = Aggregates.CaseFamily.index family\nlet selected (facts: Aggregates.DecisionFacts) = Aggregates.DecisionFacts.snapshot facts"

            Expect.equal code 0 ("Both typed positive controls: " + errors)

            let code, errors =
                compile
                    owned.FullName
                    "partial-as-complete"
                    "let wrong (facts: Aggregates.DecisionFacts) = Aggregates.CaseFamily.rows facts []"

            Expect.isTrue
                (code <> 0
                 && errors.Contains("FS0001", StringComparison.Ordinal)
                 && errors.Contains("DecisionFacts", StringComparison.Ordinal)
                 && errors.Contains("CaseFamily", StringComparison.Ordinal))
                "The compiler rejects the completeness mismatch at the consumer boundary"
        finally
            owned.Delete true)
