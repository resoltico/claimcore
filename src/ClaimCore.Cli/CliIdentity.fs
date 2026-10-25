namespace ClaimCore.Cli

open System.Reflection

module CliIdentity =
    let programName = Assembly.GetExecutingAssembly().GetName().Name |> nonNull
