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
    let create value =
        if value = Guid.Empty then None else Some(OperationId value)

    let value (OperationId value) = value
    let format id = (value id).ToString("D")

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
