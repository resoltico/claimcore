namespace ClaimCore.Cli

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application

/// Why a composed core could not be supplied for an invocation. The protocol layer renders these
/// refusals; it never names a runtime factory, a connection string, or a store.
[<RequireQualifiedAccess>]
type CoreUnavailable =
    | Configuration of message: string
    | Open of fault: RuntimeOpenFault

/// Supplies the composed typed core on demand. The CLI composition root implements this, which is
/// why no protocol type needs a reference to a runtime factory.
///
/// Acquisition is deferred rather than eager: a database-free invocation, a malformed frame, or a
/// rejected envelope must never open a runtime. An implementation returns the same composed core
/// for every later acquisition within the process.
type ICoreSupplier =
    abstract Acquire:
        cancellationToken: CancellationToken -> Task<Result<IClaimsCore, CoreUnavailable>>
