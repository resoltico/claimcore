namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// The typed application boundary; hosts choose their codecs and transport independently.
module Service =
    let create installationId store calendar principal =
        if installationId = Guid.Empty then
            invalidArg (nameof installationId) "Use an admitted installation."

        {
            InstallationId = installationId
            Store = store
            Calendar = calendar
            Principal = principal
        }

    let context = Core.context
    let get = Core.get
    let list = Core.list
    let history = Core.history
    let observe = Core.observe
    let preview = Core.previewReply
    let execute = Core.execute
    let resolve = Core.resolve
    let actors = Core.actors
    let grants = Core.grants

    /// Owner-credential composition uses the same domain decision and store transaction path.
    let provisionOwner store installationId operationId principal token =
        AuthorityExecution.provisionStore store installationId operationId principal token
