module ClaimCore.IntegrationTests.TestRegistration

open System.Reflection
open Expecto

[<Tests>]
let tests = ClaimCore.TestSupport.TestGuards.tests (Assembly.GetExecutingAssembly())
