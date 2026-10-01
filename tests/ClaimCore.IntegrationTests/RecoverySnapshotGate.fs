module internal ClaimCore.IntegrationTests.RecoverySnapshotGate

open System
open System.Threading
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions

/// Private, non-emitting instrumentation pauses the real header query after its snapshot begins.
type Gate() =
    let entered = new ManualResetEventSlim(false)
    let release = new ManualResetEventSlim(false)
    let mutable seen = 0
    let bound = TimeSpan.FromSeconds(30.)

    member _.Wait() =
        if not (entered.Wait(bound)) then
            invalidOp "Synthetic recovery header did not reach its read gate."

    member _.Release() = release.Set()

    interface ILoggerFactory with
        member _.AddProvider _ = ()

        member _.CreateLogger category =
            { new ILogger with
                member _.BeginScope(state) = NullLogger.Instance.BeginScope(state)
                member _.IsEnabled level = level >= LogLevel.Debug

                member _.Log(level, _, state, error, formatter) =
                    if category = "Npgsql.Command" && level >= LogLevel.Debug then
                        let text = formatter.Invoke(state, error)

                        if
                            text.Contains("p.canonical_request_format", StringComparison.Ordinal)
                            && Interlocked.CompareExchange(&seen, 1, 0) = 0
                        then
                            entered.Set()

                            if not (release.Wait(bound)) then
                                invalidOp "Synthetic recovery read gate was not released."
            }

        member _.Dispose() =
            release.Set()
            entered.Dispose()
            release.Dispose()
