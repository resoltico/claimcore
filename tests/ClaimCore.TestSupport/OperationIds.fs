module ClaimCore.TestSupport.OperationIds

open System

let fresh () = Guid.CreateVersion7()

/// Explicit clock inputs exercise lifetime boundaries without waiting for wall time.
let at (instant: DateTimeOffset) = Guid.CreateVersion7 instant

/// A fixture's numbered references remain stable; each first use allocates a fresh operation.
type Scope() =
    let identities = System.Collections.Concurrent.ConcurrentDictionary<int, Guid>()

    member _.Id number =
        identities.GetOrAdd(number, fun _ -> fresh ())

/// Pure decisions use an explicit observed instant and stable, independent identities.
let numberedAt (instant: DateTimeOffset) (number: int) =
    let timestamp =
        instant.ToUnixTimeMilliseconds().ToString("x12", Globalization.CultureInfo.InvariantCulture)

    Guid.Parse(
        timestamp[..7]
        + "-"
        + timestamp[8..]
        + "-7000-8000-"
        + number.ToString("x12", Globalization.CultureInfo.InvariantCulture)
    )

/// Property generators retain seeded entropy while assigning an explicit, representable issue time.
let fromEntropy (instant: DateTimeOffset) (entropy: Guid) =
    let bytes = entropy.ToByteArray(true)
    let stamp = (numberedAt instant 0).ToByteArray(true)
    Array.Copy(stamp, 0, bytes, 0, 6)
    bytes[6] <- (bytes[6] &&& 0x0fuy) ||| 0x70uy
    bytes[8] <- (bytes[8] &&& 0x3fuy) ||| 0x80uy
    Guid(bytes, true)
