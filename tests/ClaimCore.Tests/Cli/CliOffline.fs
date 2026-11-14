module internal ClaimCore.Tests.CliOffline

open ClaimCore.CommandLine

open System
open System.IO
open System.Collections.Generic
open ClaimCore.Cli

type Result =
    {
        ExitCode: int
        StandardOutput: byte array
        StandardError: byte array
    }

let invoke arguments (environment: IReadOnlyDictionary<string, string>) input =
    use output = new MemoryStream()
    use errors = new MemoryStream()

    let previous =
        environment.Keys
        |> Seq.map (fun key -> key, Environment.GetEnvironmentVariable key)
        |> Seq.toList

    try
        for pair in environment do
            Environment.SetEnvironmentVariable(pair.Key, pair.Value)

        let openInput () : Stream =
            match input with
            | Some bytes -> new MemoryStream(bytes: byte array)
            | None -> failwith "Offline command unexpectedly opened stdin"

        let code = Program.run output errors openInput (List.toArray arguments)

        {
            ExitCode = code
            StandardOutput = output.ToArray()
            StandardError = errors.ToArray()
        }
    finally
        for key, value in previous do
            Environment.SetEnvironmentVariable(key, value)
