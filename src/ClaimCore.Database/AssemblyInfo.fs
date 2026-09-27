namespace ClaimCore.Database

open System.Runtime.CompilerServices

// The synthetic qualification project may inject an independent fixture publication anchor.
// Ordinary clients cannot call the owner-only recheck engine or supply a trust source.
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
do ()
