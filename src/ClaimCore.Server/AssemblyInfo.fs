namespace ClaimCore.Server

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Tests")>]
[<assembly: InternalsVisibleTo("ClaimCore.ServerTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.TestSupport")>]
do ()
