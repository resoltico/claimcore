# People and access

Use **People & access** to add accounts, update names and contact details, and assign roles. An owner can
make these changes. An auditor can review people and access without changing them. See the
[operating guide](operating.md#access-review-and-sessions) for identity-provider and session controls.

## Add a person

Choose **Add person**, enter their sign-in identity, and give them a name. Contact details are optional.
Choose the roles they need and where each role applies. Review the complete plan before recording it.
The account, profile and selected roles are recorded together: a refused change adds none of them.

Use the person's exact issuer and subject from the sign-in provider. A service account also needs its
explicit service identity. A name or email address is not a sign-in identity. ClaimCore keeps the name
you enter; it does not take a profile name from sign-in claims.

The CLI performs the same admission in one request:

```sh
claimcore people admit --principal-kind human --issuer https://identity.example/ \
  --subject PERSON-SUBJECT --name "Alex Example" --role case-handler
```

Add another `--role` for each assignment. A role without `@` applies across the installation; for a
case-specific role use `--role case-handler@CLM-2026-0001`. The part after the first `@` is always a case
reference, even when it looks like a UUID. Choose each role and case combination only once. Up to 48
initial assignments fit in one admission. `claimcore help` lists the complete
options. When configured, the CLI profile supplies an omitted issuer; the sent request still contains it.

**Register** remains a separate option for an account that should initially have no roles. Adding or
registering someone does not change their sign-in-provider membership.

## Handle access requests

A signed-in person who is not registered sees an explanation that an owner needs to add their account.
Their access request appears in People & access. Select it to review the supplied identity and any
optional sign-in hints. Confirm the name yourself, choose roles, and add the account. Sign-in hints are
suggestions, not an authored profile or evidence of permission.

Dismiss removes a request without granting access. Requests are temporary and bounded; a later sign-in
can make a new request. Human and service identities with the same issuer and subject stay distinct.

Someone registered without roles sees **No roles assigned**. A disabled account sees **Account disabled**.
After an owner changes access, **Check access** refreshes the person's own access state. These pages do
not use Recovery as a substitute for access.

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

Use names or contact details to find people, optionally selecting a role. In the CLI,
`people list --text TEXT --role case-handler` combines current saved name/contact search with the
selected role.
Use the returned actor ID with `people grants ACTOR-ID`; do not guess IDs or use a sign-in subject as
an actor ID.

## Change a profile or roles

Open the person and choose **Edit profile**, **Grant**, **Revoke**, **Enable** or **Disable** as offered.
Review the target and exact role assignments before recording. Changing the selected person does not
retarget an existing draft or an uncertain change.

A disabled account cannot perform protected work. Existing case assignments and recorded changes stay
recorded; cases may need another responsible person. Keep at least one enabled human owner.

**Erase profile** permanently removes the saved name and contact details, including earlier saved profile
contents. The account identity, roles and accepted changes remain. Recorded changes then show an
anonymous account name. A later authored profile can give the account a name again, without restoring
the erased profile contents. Confirm this irreversible action by typing the requested change identifier.
See [erasure limits](operating.md#erasure-limits) for backups and copies outside the live service.

## Review access changes

Open **Access history**, or run `claimcore people history`, to see who changed access and what was
recorded. Actor names reflect current profiles, including profile erasure. Follow each continuation to
review more history. A case-specific role does not provide access-history access.

If a change cannot be confirmed, keep its identifier and use [delivery recovery](integrating.md#recover-an-uncertain-change).
Checking a result does not record it again. Retrying an in-memory reviewed change sends its identical
bytes; starting a different change requires a new identifier and a fresh review.
