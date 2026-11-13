namespace ClaimCore.TestSupport

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Tests")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.ServerTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.AcceptanceTests")>]
do ()
