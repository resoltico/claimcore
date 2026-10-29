namespace ClaimCore.Cli

open System.Threading
open System.Threading.Tasks
open ClaimCore.Protocol

[<NoEquality; NoComparison>]
type internal ReadPageIO =
    {
        Exchange: Invocation -> CancellationToken -> Task<CliReply>
        Deliver: Invocation -> CliReply -> unit
        Output: byte array -> unit
        Errors: byte array -> unit
    }
