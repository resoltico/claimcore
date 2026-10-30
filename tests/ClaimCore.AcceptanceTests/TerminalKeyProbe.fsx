open System

Console.Write "key-probe> "
let key = Console.ReadKey true
printfn "KEY=%d CHAR=%d MOD=%d" (int key.Key) (int key.KeyChar) (int key.Modifiers)
