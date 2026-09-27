module internal ClaimCore.IntegrationTests.FixtureWitnessWriterStore

open System
open Expecto
open ClaimCore.HostSecurity
open ClaimCore.Witness

/// A fresh isolated witness has its own capability, distinct from the base fixture.
let current writer identity =
    let path =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Isolated witness capability is absent.")

    use capability = WriterCapabilityFile.Load(path)
    capability.Use(fun material -> new Store(writer, identity, material))
