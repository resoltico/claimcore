namespace ClaimCore.Hosting

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("ClaimCore.Tests")>]
[<assembly: InternalsVisibleTo("ClaimCore.IntegrationTests")>]
[<assembly: InternalsVisibleTo("ClaimCore.WebTests")>]
do ()
