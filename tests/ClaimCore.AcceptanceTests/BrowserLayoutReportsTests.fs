module ClaimCore.AcceptanceTests.BrowserLayoutReportsTests

open System
open System.IO
open System.Text
open Expecto

let private valid =
    """{"stage":"narrow","clientWidth":320,"scrollWidth":326,"bodyWidth":320,"offenders":[{"tag":"div","className":"case-fact-card","left":0,"right":326,"width":326}],"scrollable":[]}"""

let private bytes (value: string) = Encoding.UTF8.GetBytes value

[<Tests>]
let tests =
    testList
        "safe browser layout reports"
        [
            testCase "retains only validated layout JSON with hashed names" (fun () ->
                let root = Directory.CreateTempSubdirectory("claimcore-layout-evidence-")

                try
                    let output = Directory.CreateDirectory(Path.Combine(root.FullName, "output"))
                    let report = Path.Combine(root.FullName, "report")

                    let oversized =
                        Directory.CreateDirectory(Path.Combine(output.FullName, "oversized"))

                    File.WriteAllBytes(
                        Path.Combine(oversized.FullName, "layout.json"),
                        Array.create (16 * 1024 + 1) 32uy
                    )

                    File.WriteAllText(Path.Combine(output.FullName, "layout.json"), valid)

                    File.WriteAllText(
                        Path.Combine(output.FullName, "authenticated-state.json"),
                        "private"
                    )

                    BrowserLayoutReports.retain output.FullName report "chromium"

                    let file =
                        Directory.GetFiles(Path.Combine(report, "browser-failure"))
                        |> Array.exactlyOne

                    Expect.stringStarts
                        (Path.GetFileName file |> nonNull)
                        "chromium-layout-"
                        "Only safe hashed metadata names"

                    Expect.equal
                        (File.ReadAllText file)
                        valid
                        "Existing sanitized geometry is preserved"
                finally
                    root.Delete true)
            testCase "rejects private and extra fields at every object boundary" (fun () ->
                for text in
                    [
                        valid.Replace("\"stage\":", "\"cookie\":\"private\",\"stage\":")
                        valid.Replace("\"tag\":", "\"headers\":{},\"tag\":")
                    ] do
                    Expect.isNone
                        (BrowserLayoutReports.validate (bytes text))
                        "Private fields never enter CI evidence")
            testCase "rejects oversized reports before parsing" (fun () ->
                Expect.isNone
                    (BrowserLayoutReports.validate (Array.create (16 * 1024 + 1) 32uy))
                    "The read and validator share the16KiB bound")
            testCase "rejects unknown stages tags unsafe classes and nonfinite geometry" (fun () ->
                for text in
                    [
                        valid.Replace("narrow", "private")
                        valid.Replace("\"div\"", "\"private\"")
                        valid.Replace("case-fact-card", "private value?")
                        valid.Replace("326", "1e999")
                    ] do
                    Expect.isNone
                        (BrowserLayoutReports.validate (bytes text))
                        "Only the existing safe shape is admitted")
        ]
