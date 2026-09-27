module ClaimCore.IntegrationTests.CaseListCursorProtectionTests

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting

let private principal subject =
    PrincipalKey.human "https://issuer.example.test/realms/cursor" subject
    |> Result.defaultWith (fun _ -> failtest "Synthetic principal is valid.")

let private binding =
    {
        Principal = principal "first"
        ActorId = Guid.Parse("40000000-0000-4000-8000-000000000001")
        GrantRevision = 5L
    }

let private now = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)

let private key offset =
    Array.init 32 (fun index -> byte (index + offset))

let private requiresExactAuthorityAndQuery () =
    use owner = new CaseListCursorProtection(key 1)
    let protector = owner :> ICaseListCursorProtection
    let reference = "SYNTHETIC-CURSOR-PRIVATE-REFERENCE"
    let token = CaseListCursorCodec.encode protector binding 5L 2 now reference
    let another = CaseListCursorCodec.encode protector binding 5L 2 now reference

    let read actor revision limit instant value =
        CaseListCursorCodec.decode protector actor revision limit instant value

    Expect.notEqual token another "Fresh nonce prevents deterministic token reuse"

    Expect.isFalse
        (token.Contains(reference, StringComparison.Ordinal))
        "Reference is not plaintext"

    let restored = token.Replace('-', '+').Replace('_', '/')
    let padding = String('=', (4 - restored.Length % 4) % 4)
    let wire = Convert.FromBase64String(restored + padding)

    Expect.isFalse
        (Encoding.UTF8.GetString(wire).Contains(reference, StringComparison.Ordinal))
        "Decoded token bytes also contain no reference plaintext"

    Expect.equal (read binding 5L 2 now token) (Ok reference) "Exact continuation"

    Expect.equal
        (read
            { binding with
                ActorId = Guid.NewGuid()
            }
            5L
            2
            now
            token)
        (Error())
        "A different actor cannot use the cursor"

    Expect.equal
        (read
            { binding with
                Principal = principal "second"
            }
            5L
            2
            now
            token)
        (Error())
        "The immutable principal identity is bound"

    Expect.equal (read { binding with GrantRevision = 6L } 6L 2 now token) (Error()) "Grant change"
    Expect.equal (read binding 5L 3 now token) (Error()) "Page query change"
    Expect.equal (read binding 5L 2 (now.AddMinutes 15.) token) (Error()) "Expiry boundary"
    Expect.equal (read binding 5L 2 (now.AddTicks -1L) token) (Error()) "Clock rollback"

    Expect.throws
        (fun () ->
            CaseListCursorCodec.encode protector binding 5L 2 DateTimeOffset.MaxValue reference
            |> ignore)
        "Issuance refuses an unrepresentable expiry"

let private refusesTamperAndRestart () =
    use owner = new CaseListCursorProtection(key 1)
    use successor = new CaseListCursorProtection(key 2)

    let token =
        CaseListCursorCodec.encode
            (owner :> ICaseListCursorProtection)
            binding
            5L
            2
            now
            "SYNTHETIC-001"

    let tampered = (if token[0] = 'A' then "B" else "A") + token.Substring(1)

    let read protector value =
        CaseListCursorCodec.decode protector binding 5L 2 now value

    Expect.equal
        (read (owner :> ICaseListCursorProtection) tampered)
        (Error())
        "Tampered ciphertext"

    Expect.equal
        (read (owner :> ICaseListCursorProtection) (token + "="))
        (Error())
        "Noncanonical token"

    Expect.equal
        (read (owner :> ICaseListCursorProtection) (String.replicate 769 "A"))
        (Error())
        "Oversized token"

    Expect.equal
        (read (owner :> ICaseListCursorProtection) (Unchecked.defaultof<string>))
        (Error())
        "Null token"

    Expect.equal (read (successor :> ICaseListCursorProtection) token) (Error()) "Restart key"

let tests =
    testList
        "case-list cursor protection"
        [
            testCase
                "[CC-AUTH-001] encrypted cursor binds actor grant query and expiry"
                requiresExactAuthorityAndQuery
            testCase
                "[CC-AUTH-001] cursor tamper and runtime restart refuse without plaintext"
                refusesTamperAndRestart
        ]
