import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import type { OperationSubmission } from "@contracts/client";
import { expect, test } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { applyPeople, selectedActor, syntheticPrincipals } from "./people-setup";
import { reviewedPeopleBinding } from "./people-revision";
import {
  uploadAttachment,
  downloadAttachment,
  confirmAttachmentChange,
} from "./attachment-workflow";
import { workRequest } from "./work-workflow";
import { operationId } from "../src/operation/operationId";
import { casePath } from "../src/routing/routes";

type Intent<T> = T extends OperationSubmission ? Omit<T, "operationId"> : never;

test("released and retained marker inventory through withdrawal and erasure @engine", async ({
  page,
}) => {
  test.setTimeout(150_000);
  await openAuthenticated(page);
  const suffix = randomUUID().slice(0, 8);
  const marker = (subject: string) => `SYNTHETIC_${subject}_${suffix}`;
  const fact = marker("FACT");
  const note = marker("NOTE");
  const partyName = marker("PARTY");
  const partyContact = marker("PARTY_CONTACT");
  const payment = marker("PAYMENT");
  const payee = marker("PAYEE");
  const link = marker("LINK");
  const profile = marker("PROFILE");
  const profileContact = marker("PROFILE_CONTACT");
  const decision = marker("DECISION");
  const reminder = marker("REMINDER");
  const attachment = `${marker("FILE")}.txt`;
  const payload = marker("BYTES");
  const sourceBytes = Buffer.from(payload);
  const privateValues = [
    fact,
    note,
    partyName,
    partyContact,
    payment,
    payee,
    link,
    profile,
    profileContact,
    decision,
    reminder,
    attachment,
    payload,
  ];
  const record = async (input: Intent<OperationSubmission>) => {
    const id = operationId();
    await applyPeople(page, { ...input, operationId: id });
    return id;
  };
  const open = (policyNumber: string) =>
    record({
      task: "case.open",
      binding: { kind: "CASE", expectedRevision: 0 },
      inputs: { "field:policyNumber": policyNumber },
    });
  const caseId = await open(fact);
  const peer = await open(marker("OTHER_CASE"));
  const target = { kind: "ID", caseId } as const;
  const other = { kind: "ID", caseId: peer } as const;
  const read = () => workRequest(page, "case.get", { target });
  const timeline = () => workRequest(page, "case.timeline", { target, limit: 50 });
  const lookup = (id: string) => workRequest(page, "operation.get", { operationId: id });
  const json = JSON.stringify;
  const binding = (expectedRevision: number) =>
    ({ kind: "CASE", target, expectedRevision }) as const;
  const principals = await syntheticPrincipals();
  const actor = (subject: string) =>
    selectedActor(page, { kind: "HUMAN", issuer: principals.issuer, subject });
  const owner = await actor(principals.ownerSubject);
  const profileSubject = marker("ACCOUNT");
  const profileActor = await record({
    task: "people.register",
    binding: { kind: "PEOPLE", expectedRevision: 0 },
    inputs: {
      "input:principal.kind": "HUMAN",
      "input:principal.issuer": principals.issuer,
      "input:principal.subject": profileSubject,
      "input:profile.displayName": "Synthetic profile qualification",
    },
  });
  expect(await actor(profileSubject)).toBe(profileActor);
  await record({
    task: "people.grant",
    binding: await reviewedPeopleBinding(page, profileActor),
    inputs: { "input:role": "CASE_READER", "input:scope": "CASE", "input:caseId": peer },
  });
  const grant = async (
    task: "people.grant" | "people.revoke",
    role: "AUDITOR" | "DATA_STEWARD" | "CASE_HANDLER" | "CASE_READER" | "PAYMENT_RECORDER",
    scope: "INSTALLATION" | "CASE" = "INSTALLATION",
  ) =>
    record({
      task,
      binding: await reviewedPeopleBinding(page, owner),
      inputs: {
        "input:role": role,
        "input:scope": scope,
        ...(scope === "CASE" ? { "input:caseId": caseId } : {}),
      },
    });

  await record({
    task: "note.add",
    binding: { kind: "NOTE", target },
    inputs: { "input:noteText": note },
  });
  await record({
    task: "party.add",
    binding: { kind: "PARTY", target },
    inputs: {
      "input:partyRole": "CLAIMANT",
      "input:partyName": partyName,
      "input:partyDetails": partyContact,
    },
  });
  const payeeId = await record({
    task: "party.add",
    binding: { kind: "PARTY", target },
    inputs: { "input:partyRole": "OTHER", "input:partyName": payee },
  });
  await record({
    task: "link.add",
    binding: { kind: "LINK", target },
    inputs: { "input:relatedCaseId": peer, "input:linkNote": link },
  });
  await record({
    task: "reminder.create",
    binding: { kind: "REMINDER", target },
    inputs: { "input:reminderAction": reminder, "input:reminderDueDate": "2027-12-31" },
  });
  const decisionId = await record({
    task: "case.decide",
    binding: binding(1),
    inputs: {
      "field:paymentDecisionDate": "2026-09-05",
      "field:payableAmount": "100",
      "field:payableCurrency": "EUR",
      "field:decisionBasis": decision,
    },
  });
  await record({
    task: "case.recordPayment",
    binding: binding(2),
    inputs: {
      "field:paymentDate": "2026-09-06",
      "field:paidAmount": "10",
      "field:paidCurrency": "EUR",
      "input:reference": payment,
      "input:payee": payeeId,
    },
  });
  const beforeWithdrawal = await read();
  expect(beforeWithdrawal.outcome.kind).toBe("SUCCEEDED");
  for (const value of [
    fact,
    note,
    partyName,
    partyContact,
    payment,
    payee,
    link,
    reminder,
    decision,
  ]) {
    expect(json(beforeWithdrawal)).toContain(value);
  }
  await record({ task: "case.withdrawDecision", binding: binding(3), inputs: {} });
  expect(json(await read())).not.toContain(decision);
  expect(json(await timeline())).toContain(decision);
  expect(json(await lookup(decisionId))).toContain(decision);
  const preview: OperationSubmission = {
    task: "case.amendRegistration",
    operationId: operationId(),
    binding: binding(1),
    inputs: { "field:policyNumber": marker("EDIT") },
  };
  expect(json(await workRequest(page, "operation.preview", preview))).toContain(decision);
  const search = () => workRequest(page, "case.list", { view: "ALL", limit: 10, text: fact });
  expect(json(await search())).toContain(caseId);

  await page.goto(casePath(caseId));
  const panel = page.getByRole("region", { name: "Attachments", exact: true });
  const added = await uploadAttachment(
    page,
    panel,
    attachment,
    attachment,
    "text/plain",
    sourceBytes,
  );
  await downloadAttachment(page, added.row, attachment, sourceBytes);
  expect(json(await workRequest(page, "case.attachments", { target }))).toContain(attachment);
  await added.row.getByRole("button", { name: "Withdraw attachment", exact: true }).click();
  await confirmAttachmentChange(page, "/api/operation.execute");
  await expect(panel).not.toContainText(attachment);
  expect(json(await lookup(added.receipt.operationId))).not.toContain(attachment);
  expect(json(await timeline())).not.toContain(attachment);

  const downloadTimeline = async () => {
    const result = page.waitForEvent("download");
    await page
      .locator(".case-timeline-panel")
      .getByRole("button", { name: "Download CSV" })
      .click();
    return (await result).path();
  };
  const savedPath = await downloadTimeline();
  const saved = await readFile(savedPath);
  for (const value of [fact, note, partyName, payment, decision, reminder]) {
    expect(saved.toString("utf8")).toContain(value);
  }

  await grant("people.grant", "CASE_READER", "CASE");
  await grant("people.revoke", "CASE_HANDLER");
  await grant("people.revoke", "PAYMENT_RECORDER");
  try {
    const narrowed = await timeline();
    expect(narrowed.outcome.kind).toBe("SUCCEEDED");
    expect(json(narrowed)).toContain("SUMMARY");
    expect(json(narrowed)).not.toContain(decision);
    expect(json(await read())).not.toContain(link);
    expect((await workRequest(page, "case.get", { target: other })).outcome.kind).toBe("REJECTED");
    expect(json(await lookup(decisionId))).not.toContain(decision);
    expect(json(await workRequest(page, "operation.preview", preview))).not.toContain(decision);
  } finally {
    await grant("people.grant", "CASE_HANDLER");
    await grant("people.grant", "PAYMENT_RECORDER");
    await grant("people.revoke", "CASE_READER", "CASE");
  }

  await grant("people.grant", "AUDITOR");
  await grant("people.grant", "DATA_STEWARD");
  try {
    const detailEvidence = async () => {
      const result = await workRequest(page, "disclosure.trail", {
        actorId: owner,
        caseId,
        kind: "CASE_READ",
        limit: 50,
      });
      if (result.outcome.kind !== "SUCCEEDED") {
        throw new Error("Expected authorized case detail evidence.");
      }
      return result.outcome.data.items;
    };
    const previousIds = new Set((await detailEvidence()).map(({ id }) => id));
    const webRead = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/case.get",
    );
    await page.reload();
    expect((await webRead).ok()).toBe(true);
    await expect(page.locator(".case-workspace")).toContainText(fact);
    const webEvents = (await detailEvidence()).filter(({ id }) => !previousIds.has(id));
    expect(webEvents.length).toBeGreaterThan(0);
    for (const { event } of webEvents) {
      expect(event).toEqual({
        actorId: owner,
        kind: "CASE_READ",
        caseId,
        attachmentId: null,
        outcome: "SUCCEEDED",
        export: null,
      });
    }

    const profileOperation = await record({
      task: "people.setProfile",
      binding: await reviewedPeopleBinding(page, profileActor),
      inputs: { "input:profile.displayName": profile, "input:profile.contact": profileContact },
    });
    const directory = () => workRequest(page, "people.list", { text: profile, limit: 50 });
    const listedProfile = json(await directory());
    expect(listedProfile).toContain(profile);
    expect(listedProfile).toContain(profileContact);
    await record({
      task: "people.eraseProfile",
      binding: await reviewedPeopleBinding(page, profileActor),
      inputs: {},
    });
    expect(json(await directory())).not.toContain(profile);
    expect(json(await lookup(profileOperation))).not.toContain(profileContact);
    expect(
      json(await workRequest(page, "people.grants", { actorId: profileActor, limit: 50 })),
    ).toContain(peer);

    const current = await read();
    if (current.outcome.kind !== "SUCCEEDED") {
      throw new Error("Expected a live case and family sequence.");
    }
    const { caseReference, revision, familySequence } = current.outcome.data;
    await record({
      task: "case.erase",
      binding: { ...binding(revision), expectedFamilySequence: familySequence },
      inputs: {},
    });
    const erased = await read();
    expect(json(erased)).toContain(caseReference);
    expect(json(erased)).toContain(caseId);
    expect(json(erased)).toContain("ERASED");
    const otherCase = await workRequest(page, "case.get", { target: other });
    expect(json(otherCase)).toContain(marker("OTHER_CASE"));
    const outputs = [
      erased,
      await timeline(),
      await search(),
      await lookup(caseId),
      await lookup(decisionId),
      await workRequest(page, "operation.preview", preview),
      otherCase,
      await workRequest(page, "disclosure.trail", { caseId, limit: 50 }),
    ];
    for (const result of outputs) {
      for (const value of privateValues) {
        expect(json(result)).not.toContain(value);
      }
    }
    expect(json(outputs.at(-1))).toContain("EXPORT");
    expect(json(outputs.at(-1))).toContain("ATTACHMENT_DOWNLOAD");
    expect(await readFile(savedPath)).toEqual(saved);
    await page.reload();
    const fresh = await readFile(await downloadTimeline(), "utf8");
    for (const value of privateValues) {
      expect(fresh).not.toContain(value);
    }
    expect(fresh).toContain(caseReference);
  } finally {
    await grant("people.revoke", "DATA_STEWARD");
    await grant("people.revoke", "AUDITOR");
  }
});
