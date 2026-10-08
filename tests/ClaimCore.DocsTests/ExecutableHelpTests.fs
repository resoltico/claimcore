module ClaimCore.DocsTests.ExecutableHelpTests

open System.IO
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private prepare (repository: TempRepository) =
    for product in [ "ClaimCore.Cli"; "ClaimCore.Database"; "ClaimCore.Web" ] do
        repository.Write($"src/{product}/{product}.fsproj", "<Project />\n") |> ignore

let tests =
    testList
        "executable help prerequisites"
        [
            testCase "a failed batched help build prevents target resolution and execution"
            <| fun _ ->
                use repository = new TempRepository()
                prepare repository
                let runner = QueueRunner([ processOutput 17 "" "" ])

                let errors =
                    ExecutableHelp.build
                        {
                            Root = repository.Root
                            Processes = runner
                        }
                    |> requireError

                Expect.equal runner.Requests.Length 1 "No product executes after a failed build"
                let solution = runner.Requests.Head.Arguments |> List.item 1
                Expect.isTrue (File.Exists solution) "Failed requirements remain diagnosable"

                Expect.isTrue
                    (errors |> List.exists (fun error -> error.Message.Contains(solution)))
                    "The bounded diagnostic names retained requirements"

                let directory = Path.GetDirectoryName solution |> Option.ofObj |> Option.get
                Directory.Delete(directory, true)

            testCase "a linked help project is refused before any producer executes"
            <| fun _ ->
                use repository = new TempRepository()
                prepare repository

                let project =
                    Path.Combine(repository.Path, "src/ClaimCore.Cli/ClaimCore.Cli.fsproj")

                let target = repository.Write("private-project.fsproj", "<Project />\n")
                File.Delete project
                File.CreateSymbolicLink(project, target) |> ignore
                let runner = QueueRunner([])

                ExecutableHelp.build
                    {
                        Root = repository.Root
                        Processes = runner
                    }
                |> requireError
                |> ignore

                Expect.isEmpty runner.Requests "No build follows an unsafe project"
        ]
