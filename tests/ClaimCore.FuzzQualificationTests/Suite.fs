module ClaimCore.FuzzQualificationTests.Suite

open Expecto

[<Tests>]
let tests = testList "ClaimCore fuzz qualification" [ Totality.tests ]
