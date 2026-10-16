module ClaimCore.AcceptanceTests.FixtureCleanup

open System.IO
open System
open Expecto

let internal finish = ClaimCore.TestSupport.FixtureDisposal.finish

type internal OwnedDirectory() =
    let root = FixtureFiles.directory ()

    let generated =
        [
            "ClaimCore.Cli"
            "ClaimCore.Database"
            "ClaimCore.Web"
            "client-source"
            "different-client"
        ]

    member _.Path = root

    member _.RemoveGenerated(candidate: string) =
        if Path.GetFullPath candidate <> root || DirectoryInfo(root).LinkTarget <> null then
            invalidArg (nameof candidate) "Cleanup requires this newly created fixture directory."

        let paths = generated |> List.map (fun name -> Path.Combine(root, name))

        if paths |> List.exists (fun path -> DirectoryInfo(path).LinkTarget <> null) then
            invalidOp "Generated fixture directories must not redirect cleanup."

        for path in paths do
            if Directory.Exists path then
                Directory.Delete(path, true)

[<Tests>]
let tests =
    testCase
        "fixture cleanup removes generated copies, retains logs and refuses another scope"
        (fun () ->
            let owned = OwnedDirectory()
            let other = OwnedDirectory()

            try
                for name in
                    [
                        "ClaimCore.Cli"
                        "ClaimCore.Database"
                        "ClaimCore.Web"
                        "client-source"
                        "different-client"
                    ] do
                    let path = Path.Combine(owned.Path, name)
                    Directory.CreateDirectory path |> ignore
                    File.WriteAllText(Path.Combine(path, "synthetic-output"), "reproducible")

                let log = FixtureFiles.text owned.Path "web.stderr" "synthetic diagnostic"
                let outside = FixtureFiles.text other.Path "evidence" "keep"

                Expect.throws
                    (fun () -> owned.RemoveGenerated other.Path)
                    "Another fixture cannot be cleaned"

                let link = Path.Combine(owned.Path, "different-client")
                Directory.Delete(link, true)
                Directory.CreateSymbolicLink(link, other.Path) |> ignore

                Expect.throws
                    (fun () -> owned.RemoveGenerated owned.Path)
                    "A redirected tree refuses"

                Expect.equal (File.ReadAllText outside) "keep" "Outside evidence survives refusal"
                Directory.Delete link
                owned.RemoveGenerated owned.Path
                Expect.equal (Directory.GetDirectories owned.Path) [||] "Generated trees removed"
                Expect.equal (File.ReadAllText log) "synthetic diagnostic" "Failure logs remain"
            finally
                Directory.Delete(owned.Path, true)
                Directory.Delete(other.Path, true))

[<Tests>]
let disposalTests =
    testCase
        "fixture cleanup runs after disposal failure and preserves every teardown error"
        (fun () ->
            let owned = OwnedDirectory()

            try
                let generated = Path.Combine(owned.Path, "ClaimCore.Web")
                Directory.CreateDirectory generated |> ignore
                let log = FixtureFiles.text owned.Path "web.stderr" "synthetic diagnostic"
                let failure = IOException("Synthetic database disposal failure")
                let mutable disposed = false

                let actual =
                    try
                        finish
                            [
                                (fun () -> raise failure)
                                (fun () -> disposed <- true)
                                (fun () -> owned.RemoveGenerated owned.Path)
                            ]

                        None
                    with error ->
                        Some error

                Expect.isTrue disposed "Subsequent owned disposables execute"
                Expect.isFalse (Directory.Exists generated) "Generated trees removed after failure"

                Expect.equal
                    (File.ReadAllText log)
                    "synthetic diagnostic"
                    "Failure evidence survives"

                Expect.isTrue
                    (actual |> Option.exists (fun error -> obj.ReferenceEquals(error, failure)))
                    "Original failure retained"

                try
                    finish
                        [
                            (fun () -> raise failure)
                            (fun () -> invalidOp "Synthetic cleanup failure")
                        ]

                    failtest "Both teardown failures must remain visible"
                with :? AggregateException as errors ->
                    Expect.equal errors.InnerExceptions.Count 2 "Every failure retained"
            finally
                Directory.Delete(owned.Path, true))
