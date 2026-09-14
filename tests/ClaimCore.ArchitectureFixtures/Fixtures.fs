namespace ClaimCore.ArchitectureFixtures

open System.Runtime.CompilerServices

/// This inert target is never executed by inspection. Dependencies on it are intentional negatives.
type Boundary =
    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Forbidden<'T>(value: 'T) = value

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    static member Allowed<'T>(value: 'T) = value

module GoodFunction =
    let invoke (value: int) = value

module GoodGeneric =
    type Holder<'T>(value: 'T) =
        member _.Read() = value

module GoodClosure =
    let create (value: int) = fun () -> value

module GoodNested =
    module Inner =
        type Holder(value: int) =
            member _.Read() = value

module GoodTask =
    let invoke (value: int) =
        task {
            do! System.Threading.Tasks.Task.Yield()
            return value
        }

module GoodAsync =
    let invoke (value: int) =
        async {
            do! Async.SwitchToThreadPool()
            return value
        }

module GoodSequence =
    let read (value: int) = seq { yield value }

module BadFunction =
    let invoke (value: int) = Boundary.Forbidden value

module BadGeneric =
    type Holder<'T>(value: 'T) =
        member _.Read() = Boundary.Forbidden value

module BadClosure =
    let create (value: int) = fun () -> Boundary.Forbidden value

module BadNested =
    module Inner =
        type Holder(value: int) =
            member _.Read() = Boundary.Forbidden value

module BadTask =
    let invoke (value: int) =
        task {
            do! System.Threading.Tasks.Task.Yield()
            return Boundary.Forbidden value
        }

module BadAsync =
    let invoke (value: int) =
        async {
            do! Async.SwitchToThreadPool()
            return Boundary.Forbidden value
        }

module BadSequence =
    let read (value: int) = seq { yield Boundary.Forbidden value }

module GoodSignature =
    [<NoEquality; NoComparison>]
    type Holder = { Value: int }

module BadSignature =
    [<NoEquality; NoComparison>]
    type Holder = { Value: Boundary }

module GoodMethod =
    let invoke (value: int) = Boundary.Allowed value

module BadMethod =
    let invoke (value: int) = Boundary.Forbidden value

module GoodClock =
    let invoke (value: System.DateTime) = value.Date

module BadClock =
    let invoke () = System.DateTime.UtcNow

module GoodIo =
    let invoke (value: string) = value.Length

module BadIo =
    let invoke (path: string) = System.IO.File.ReadAllText(path).Length

module GoodUnion =
    [<NoEquality; NoComparison>]
    type Value = Value of int

module BadUnion =
    [<NoEquality; NoComparison>]
    type Value = Value of Boundary

module GoodInterface =
    type IRead =
        abstract Read: unit -> int

module BadInterface =
    type IRead =
        abstract Read: unit -> Boundary
