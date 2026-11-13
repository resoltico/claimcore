import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../support/presentation-test-support";
import { PeopleGrants } from "../../../src/views/PeopleGrants";
import { isResponse, type HumanReadDocument_people_grants } from "@contracts/client";

const actorId = "00000000-0000-4000-8000-000000000001";
const caseId = "00000000-0000-4000-8000-000000000002";
const document: HumanReadDocument_people_grants = {
  screen: {
    regions: [
      {
        role: "ACCOUNT",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "actor",
              labelId: "field.profile.displayName.label",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Core person caption Ω" },
            },
            {
              key: "responsibleCases",
              labelId: "label.responsibleCases",
              prominence: "SECONDARY",
              value: { kind: "TEXT", value: "Core responsibility caption Δ" },
            },
            {
              key: "other-count",
              labelId: "label.details",
              prominence: "SECONDARY",
              value: { kind: "COUNT", value: 17 },
            },
          ],
        },
      },
      {
        role: "ACCESS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "role", labelId: "field.role.label", prominence: "PRIMARY", isMoney: false },
          ],
          rows: [
            { key: "selected-grant", values: [{ kind: "TEXT", value: "Core grant caption Ψ" }] },
          ],
        },
      },
    ],
  },
  context: {
    kind: "PERSON",
    actorId,
    principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "person" },
    tasks: ["people.revoke"],
    profileSeed: null,
    nextCursor: null,
    revocations: [{ key: "selected-grant", role: "CASE_READER", scope: { kind: "CASE", caseId } }],
    responsibleCases: {
      view: "ALL",
      responsibleActor: actorId,
      lifecycle: "ACTIVE",
      status: "OPENED",
    },
  },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (personDocument: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: personDocument },
});
const response = (personDocument: unknown) =>
  new Response(JSON.stringify(reply(personDocument)), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
afterEach(() => vi.unstubAllGlobals());
it("renders an opaque Core person title and binds revoke/navigation to typed controls without a business DTO", async () => {
  expect(isResponse("people.grants", reply(document))).toBe(true);
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(document)));
  const revoke = vi.fn();
  const observed = vi.fn();
  render(
    <PeopleGrants
      actorId={actorId}
      onBack={vi.fn()}
      onDisclosureRefused={vi.fn()}
      onActor={observed}
      onRevoke={revoke}
    />,
  );
  await screen.findByRole("dialog", { name: "Core person caption Ω" });
  expect(screen.getByText("Core grant caption Ψ")).toBeVisible();
  const link = screen.getByRole("link", { name: /Core responsibility caption Δ/u });
  const href = new URL(link.getAttribute("href")!, "https://example.test");
  expect(href.searchParams.get("view")).toBe("ALL");
  expect(href.searchParams.get("responsibleActor")).toBe(actorId);
  expect(href.searchParams.get("lifecycle")).toBe("ACTIVE");
  expect(href.searchParams.get("status")).toBe("OPENED");
  expect(screen.queryByRole("link", { name: /17/u })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /revoke/iu }));
  expect(revoke).toHaveBeenCalledExactlyOnceWith(document.context.revocations[0]);
  expect(observed).toHaveBeenCalledWith(document);
});
it("rejects missing/unoffered revocations and a responsibility destination belonging to another person", () => {
  for (const context of [
    { ...document.context, revocations: [] },
    { ...document.context, tasks: [] },
    {
      ...document.context,
      revocations: [...document.context.revocations, ...document.context.revocations],
    },
    {
      ...document.context,
      responsibleCases: { ...document.context.responsibleCases, responsibleActor: caseId },
    },
  ]) {
    expect(isResponse("people.grants", reply({ ...document, context }))).toBe(false);
  }
});
it("does not publish a person document for a different requested actor", async () => {
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(document)));
  const observed = vi.fn();
  render(
    <PeopleGrants
      actorId={caseId}
      onBack={vi.fn()}
      onDisclosureRefused={vi.fn()}
      onActor={observed}
    />,
  );
  await screen.findByRole("alert");
  expect(screen.queryByText("Core person caption Ω")).not.toBeInTheDocument();
  expect(observed).not.toHaveBeenCalled();
});
