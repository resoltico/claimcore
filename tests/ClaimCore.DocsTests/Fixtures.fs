module ClaimCore.DocsTests.Fixtures

open System
open System.Collections.Generic
open System.IO
open System.Text
open ClaimCore.Docs

type TempRepository() =
    let path =
        Path.Combine(Path.GetTempPath(), "claimcore-doc-tests-" + Guid.NewGuid().ToString("N"))

    do
        Directory.CreateDirectory(path) |> ignore

        File.WriteAllText(
            Path.Combine(path, "ClaimCore.slnx"),
            "<Solution />\n",
            UTF8Encoding(false)
        )

        File.WriteAllText(
            Path.Combine(path, "Directory.Build.props"),
            "<Project />\n",
            UTF8Encoding(false)
        )

    member _.Path = path
    member _.Root = RepositoryRoot.CreateForTests(path)

    member _.Write(relative: string, content: string) =
        let target = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))

        let directory =
            Path.GetDirectoryName(target) |> Option.ofObj |> Option.defaultValue path

        Directory.CreateDirectory(directory) |> ignore
        File.WriteAllText(target, content, UTF8Encoding(false))
        target

    member _.WriteBytes(relative: string, content: byte array) =
        let target = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar))

        let directory =
            Path.GetDirectoryName(target) |> Option.ofObj |> Option.defaultValue path

        Directory.CreateDirectory(directory) |> ignore
        File.WriteAllBytes(target, content)
        target

    member _.Read(relative: string) =
        File.ReadAllText(Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar)))

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists(path) then
                Directory.Delete(path, true)

type QueueRunner(outputs: ProcessOutput list) =
    let queue = Queue<ProcessOutput>(outputs)
    let requests = ResizeArray<ProcessRequest>()

    member _.Requests = List.ofSeq requests

    interface IProcessRunner with
        member _.Run request =
            requests.Add(request)

            if queue.Count = 0 then
                Error "Unexpected process invocation."
            else
                Ok(queue.Dequeue())

let processOutput exitCode stdout stderr =
    {
        ExitCode = exitCode
        StandardOutput = stdout
        StandardError = stderr
    }

let markdown relative content =
    MarkdownModel.fromText relative (Path.Combine("/virtual", relative)) content

let requireOk result =
    match result with
    | Ok value -> value
    | Error _ -> failwith "Expected success."

let requireError result =
    match result with
    | Error value -> value
    | Ok _ -> failwith "Expected failure."
