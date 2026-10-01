module ClaimCore.ArchitectureTests.PolicyInputTests

open System
open System.IO
open Expecto

let private source () =
    File.ReadAllText(ProductPolicy.manifestPath ())

let private rejects transformation =
    Expect.throws
        (fun () -> ProductPolicy.parse (transformation (source ())) |> ignore)
        "Malformed policy cannot reach compiled inspection"

let tests =
    testList
        "architecture policy admission"
        [
            testCase "reviewed policy parses completely" (fun () ->
                Expect.equal
                    (ProductPolicy.parse (source ()) |> List.length)
                    ProductPolicy.components.Length
                    "Every component")
            testCase "duplicate and unknown root fields are refused" (fun () ->
                rejects (fun text ->
                    text.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"))

                rejects (fun text ->
                    text.Replace("\"version\": 1", "\"version\": 1, \"typo\": true")))
            testCase "unknown component fields and duplicate properties are refused" (fun () ->
                rejects (fun text ->
                    text.Replace(
                        "\"layer\": \"core\"",
                        "\"layer\": \"core\", \"layer\": \"core\""
                    ))

                rejects (fun text ->
                    text.Replace(
                        "\"layer\": \"core\"",
                        "\"layer\": \"core\", \"allowEverything\": true"
                    )))
            testCase "escaped and wrongly classified project paths are refused" (fun () ->
                for path in
                    [
                        "../ClaimCore.Domain.fsproj"
                        "src/../ClaimCore.Domain.fsproj"
                        "eng/ClaimCore.Domain/ClaimCore.Domain.fsproj"
                    ] do
                    rejects (fun text ->
                        text.Replace("src/ClaimCore.Domain/ClaimCore.Domain.fsproj", path)))
            testCase "malformed list entries cannot disappear during parsing" (fun () ->
                rejects (fun text -> text.Replace("\"dependsOn\": []", "\"dependsOn\": [false]")))
        ]
