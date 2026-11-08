namespace ClaimCore.Domain

open System
open System.Globalization

/// Exact integer range shared by Domain positions and interoperable JSON records.
module SafeInteger =
    [<Literal>]
    let Maximum = 9007199254740991L

    let internal successor value =
        let next = Checked.(+) value 1L

        if next > Maximum then
            raise (OverflowException "The successor is outside the exact-integer format.")

        next

[<Struct>]
type OperationId = private OperationId of Guid

module OperationId =
    let private timestamp (bytes: byte array) =
        bytes[0..5] |> Array.fold (fun total value -> (total <<< 8) ||| int64 value) 0L

    let create (value: Guid) =
        let bytes = value.ToByteArray(true)

        if
            bytes[6] >>> 4 = 7uy
            && bytes[8] &&& 0xc0uy = 0x80uy
            && timestamp bytes <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
        then
            Some(OperationId value)
        else
            None

    let value (OperationId value) = value
    let format id = (value id).ToString("D")

    let issuedAt id =
        (value id).ToByteArray(true)
        |> timestamp
        |> DateTimeOffset.FromUnixTimeMilliseconds

/// Execution time is the provider instant obtained under the ledger-head lock.
module OperationLifetime =
    [<Literal>]
    let WindowMilliseconds = 600000L

    [<Literal>]
    let FutureAllowanceMilliseconds = 120000L

    let window = TimeSpan.FromMilliseconds(float WindowMilliseconds)
    let futureAllowance = TimeSpan.FromMilliseconds(float FutureAllowanceMilliseconds)
    let finalAt operation = OperationId.issuedAt operation + window

    let admits (observedAt: DateTimeOffset) operation =
        let age = observedAt - OperationId.issuedAt operation
        age <= window && age >= -futureAllowance

[<Struct>]
type ActorId = private ActorId of Guid

module ActorId =
    let create value =
        if value = Guid.Empty then None else Some(ActorId value)

    let value (ActorId value) = value
    let ofOperationId operation = ActorId(OperationId.value operation)
    let format id = (value id).ToString("D")

[<Struct>]
type CaseId = private CaseId of Guid

module CaseId =
    let create value =
        if value = Guid.Empty then None else Some(CaseId value)

    let value (CaseId value) = value
    let ofOperationId operation = CaseId(OperationId.value operation)
    let format id = (value id).ToString("D")

[<Struct>]
type Revision = private Revision of int64

module Revision =
    [<Literal>]
    let Maximum = SafeInteger.Maximum

    let create value =
        if value < 0L || value > Maximum then
            None
        else
            Some(Revision value)

    let value (Revision value) = value

    let format position =
        (value position).ToString(CultureInfo.InvariantCulture)

    let zero = Revision 0L
    let first = Revision 1L

    let successor position =
        Revision(SafeInteger.successor (value position))

[<Struct>]
type Sequence = private Sequence of int64

module Sequence =
    [<Literal>]
    let Maximum = SafeInteger.Maximum

    let create value =
        if value < 0L || value > Maximum then
            None
        else
            Some(Sequence value)

    let value (Sequence value) = value

    let format position =
        (value position).ToString(CultureInfo.InvariantCulture)

    let zero = Sequence 0L
    let first = Sequence 1L

    let successor position =
        Sequence(SafeInteger.successor (value position))
