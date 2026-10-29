module ClaimCore.Tests.TestRegistration

open System.Reflection
open Expecto

[<Tests>]
let tests =
    GoldenPreparation.prepare ()
    ClaimCore.TestSupport.TestGuards.tests (Assembly.GetExecutingAssembly())
