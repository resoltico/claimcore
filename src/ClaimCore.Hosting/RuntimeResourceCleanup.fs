namespace ClaimCore.Hosting

open System

/// Try every owned cleanup, without retaining or reflecting provider exception details.
module internal RuntimeResourceCleanup =
    let disposeAll (resources: IDisposable seq) =
        let mutable failed = false

        for resource in resources do
            try
                resource.Dispose()
            with _ ->
                failed <- true

        if failed then
            invalidOp "ClaimCore runtime resource cleanup failed."
