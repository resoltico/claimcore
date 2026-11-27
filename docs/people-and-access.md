# People and access

Open **People and access** from Administration in the sidebar or from **More** on a phone.
**Add person** lists the available registration and access-request operations. Choose a row's detail
in its action menu. Access history and the access and export log show an event per row and open its
details in a dialog. Settings groups language, appearance, browser sessions and **About ClaimCore**.

Use **People and access** to add accounts, update names and contact details, and assign roles. An owner can
make these changes. An auditor can review people and access without changing them. See the
[operating guide](operating.md#access-review-and-sessions) for identity-provider and session controls.

If the identity provider is temporarily unavailable, existing ClaimCore sessions continue
under their normal limits. Sign out still ends the local session. ClaimCore reports when the
provider handoff could not proceed.

## Digest address

An Owner can use the profile edit action to set or clear a person's daily reminder digest address.
The address controls delivery of the daily counts and is cleared by profile erasure. See
[Daily reminder digest](operating.md#daily-reminder-digest) for the service settings.

## Add a person

Choose **Add person**, enter their sign-in identity, and give them a name. Contact details are optional.
Choose the roles they need and where each role applies. Review the complete plan before recording it.
The account, profile and selected roles are recorded together: a refused change adds none of them.

Use the person's exact issuer and subject from the sign-in provider. A service account also needs its
explicit service identity. A name or email address is not a sign-in identity. ClaimCore keeps the name
you enter; it does not take a profile name from sign-in claims.

Confirm the exact sign-in ID shown in the Web review. In an interactive CLI review, type that same
value at the prompt. For CLI automation, pass it as `--confirm`. If the prompt shows the ID as a
quoted string, include the quotes and any escape characters displayed.

The CLI performs the same admission in one request:

```sh
claimcore people admit --principal-kind human --issuer https://identity.example/ \
  --subject PERSON-SUBJECT --confirm PERSON-SUBJECT --name "Alex Example" --role case-handler
```

Add another `--role` for each assignment. A role without `@` applies across the installation; for a
case-specific role use `--role case-handler@CLM-2026-0001`. The part after the first `@` is always a case
reference, even when it looks like a UUID. Choose each role and case combination only once. Up to 48
initial assignments fit in one admission. `claimcore help` lists the complete
options. When configured, the CLI profile supplies an omitted issuer; the sent request still contains it.

Use **Register account** for an account that should initially have no roles. Adding or
registering someone does not change their sign-in-provider membership.

## Handle access requests

Sign in with an identity that has never been bound to an account, then choose **Request access**.
A service can submit the same request with `claimcore people requests submit`. Checking status does not
send a request. Sending again refreshes the current request. An owner sees it in People and access. Review the issuer and subject as the sign-in
identity. The identity provider supplies any name and username hints without verification. An email
hint shows whether the provider verified that address. Confirm the identity with a trusted source,
check the suggested profile details, choose roles, and type the subject to add the account.

Dismiss removes a request without granting access. An eligible caller can submit a new request. Human
and service identities with the same issuer and subject stay distinct. If the request list changes
while you page through it, use **Try again** to restart from the first page.

Someone registered without roles sees **No roles assigned**. A disabled account sees **Account disabled**.
**Check access** refreshes the person's own status. After admission or re-enablement,
sign out and sign in again. Profile and grant changes take effect on the next authorized action
with the same current credential.

For a case-specific role, choose reference or ID and enter only that target. Switching the selection
replaces the active target; blank or malformed active text is retained for a precise correction. An
existing seeded grant keeps its supplied target. All-cases roles send neither selector.

The directory uses the full available width; person details and changes open in centered dialogs. Empty access
requests omit their table. No-pending status requires all collected pages to be empty with no further
page; a failed or incomplete read does not establish that there are no requests.

## Find a person and review access

The list shows saved names, account type, assigned roles, account state and contact details. Names also
appear on recorded changes. **Enabled** means the account is enabled and has assigned roles; what it
can do still depends on those roles and where they apply. A disabled account and its roles remain
reviewable.

Select a person to review their profile, responsibilities and all grants, following **Load more** when offered. A row summary is not a full grant list.

**Responsible for** counts active open cases assigned to that person within *your* case-read access.
Follow its link to the filtered Cases view. Use each row's **Reassign** action to hand over a case: choose
the new responsible person, review and confirm once. Each row retains its own work version; a concurrent
change requires a fresh review. A visible zero means no matching cases; an omitted count means your roles do not
provide that case visibility. Responsibility never grants access.

Enter a name or contact detail in **Search people** and press Enter. The **Role** filter applies when you select it. In the CLI,
`people list --text TEXT --role case-handler` combines current saved name/contact search with the
selected role.
Use the returned actor ID with `people grants ACTOR-ID` and the person-change verbs (grant, revoke,
set-profile, enable, disable and erase-profile); do not guess IDs or use a sign-in subject as
an actor ID.

## Change a sign-in identity

An owner can use **Change sign-in identity** to enter a person's new issuer and subject. Keep the
same account type. The person keeps their actor ID, roles, case responsibilities and recorded work.
Earlier identities remain reserved to the same person and can only be returned to that account.
An earlier identity receives private sign-in guidance and cannot create an access request.
Rebinding retires the account's protected credentials. The person must sign in with the new identity.
A different signed-in Owner must change an enabled Owner's identity.

If you need to restore owner access through the database owner connection, see
[Recover owner access](operating.md#recover-owner-access).

## Change a profile or roles

Open the person and choose **Update profile**, **Grant role**, **Revoke role**, **Enable account** or
**Disable account** as offered.
Review the target and exact role assignments before recording. Changing the selected person does not
retarget an existing draft or an uncertain change. Each existing-person change freezes the account
version you reviewed. A concurrent profile or access change requires a fresh review; it cannot silently
restore access or erase a newer profile. Every accepted logical change, including a deliberate grant
reaffirmation, advances the account version once. Exact accepted retries do not advance it.

A disabled account cannot perform protected work. Existing case assignments and recorded changes stay
recorded; cases may need another responsible person. Keep at least one enabled human owner.

**Erase profile** permanently removes the saved name, contact details and digest address, including earlier saved profile
contents. The account identity, roles and accepted changes remain. Recorded changes then show an
anonymous account name. A later authored profile can give the account a name again, without restoring
the erased profile contents. Confirm this irreversible action by typing the requested change identifier.
See [erasure limits](operating.md#erasure-limits) for backups and copies outside the live service.

## Review access changes

In **Access history**, use **Person** to search the directory or enter an exact account ID. Open **Access history**, or run `claimcore people history`, to see who changed access and what was
recorded. Actor names reflect current profiles, including profile erasure. Enter an **Account ID** in the
toolbar and choose **Apply filters** to restrict the history to changes affecting that account. To see all
accessible activity again, clear the field and apply the filter. **Load more** and **Download CSV** use the
applied selection. The CSV starts at its first page and applies the same disclosure rules throughout.
A case-specific role does not provide access-history access.

If a change cannot be confirmed, keep its identifier and use [delivery recovery](integrating.md#recover-an-uncertain-change).
Checking a result does not record it again. Retrying an in-memory reviewed change sends its identical
bytes; starting a different change requires a new identifier and a fresh review.

## Read the disclosure trail

An Auditor with an installation-wide grant can open **Access and export log** or run
`claimcore disclosure trail`. The trail identifies the actor and targets by ID and includes
reads of the trail itself. See [disclosure trail operations](operating.md#disclosure-trail).

## End browser sessions

In Settings or on the access screen, **End my ClaimCore browser sessions** ends all of your
ClaimCore browser sessions, including the current one. The access screen exposes this action before
registration and to roleless accounts with current credentials. A bound account that needs to
sign in again can still sign out of its current session. Ordinary **Sign out** ends the current ClaimCore browser session
and hands off to the identity provider when its sign-out endpoint is available.

An owner can select a person and choose **End this person’s ClaimCore browser sessions**. Permission
is checked again when deletion runs. A confirmed result reports the number of stored session tickets
removed, including tickets for earlier reserved identities and tickets issued before registration.
A former identity can end only its own verified-principal tickets. On an unconfirmed result,
choose **Resume unconfirmed termination** in the same tab after signing in with current access.
This checks for the saved count and leaves newer sessions open. Choose **Start a new termination**
to end newer sessions deliberately. The same tab keeps earlier unresolved IDs while their results
can still be recovered. A server clock observation can place a request outside the current recovery
window. Web keeps its ID until a committed cutoff confirms that it has expired.

Web rechecks your current access when you resume or when saved requests for this register reach
their limit. If expired requests fill the tab, confirming a new termination also asks the server to
confirm retirement before sending the termination. Retirement leaves browser sessions open.
Saving a new request clears independently unreadable entries and IDs older than the committed
cutoff for this register. You can also forget a matching request after confirmed expiry.
Other registers keep their own saved requests.

The two bulk actions affect ClaimCore browser tickets only. Identity-provider sessions, credentials
and independent bearer tokens remain valid. Previously admitted work can finish.
Only a sign-in flow started after the termination cut can create a new ticket for a selected identity. See the [session lock and commit contract](operating.md#access-review-and-sessions).

The CLI exposes the same nonledger commands:

```sh
claimcore session terminate-own REQUEST-ID --installation-id REGISTER-ID --dry-run
claimcore people terminate-sessions ACTOR-ID REQUEST-ID --installation-id REGISTER-ID --dry-run
```

Use the installation ID from the requester termination scope. Keep it with the prepared frame
through dispatch and recovery.

Use a fresh UUIDv7 request ID for a new command. Omit `--dry-run` to send it. Record the ID and
selected account before sending. Use `--settle-only true` with that ID and selection to check
the saved result after an interruption.
