import { within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import type { RecoveryItem } from "@contracts/client";
import enCommands from "../../src/ClaimCore.Contract/Localization/en.command.json";
import lvCommands from "../../src/ClaimCore.Contract/Localization/lv.command.json";
import arCommands from "../../src/ClaimCore.Contract/Localization/ar.command.json";
import enAuthority from "../../src/ClaimCore.Contract/Localization/en.authority.json";
import lvAuthority from "../../src/ClaimCore.Contract/Localization/lv.authority.json";
import arAuthority from "../../src/ClaimCore.Contract/Localization/ar.authority.json";
import { service } from "../src/api/service";
import { preferenceKey } from "../src/presentation/preferences";
import { RecoveryRequests } from "../src/views/operation/RecoveryRequests";
import { render, screen } from "./presentation-test-support";
import { definition } from "./service-ui.fixtures";

const catalogs = {
  en: { ...enCommands, ...enAuthority },
  lv: { ...lvCommands, ...lvAuthority },
  ar: { ...arCommands, ...arAuthority },
};
const command = definition.descriptors.commands[0]!;
const authority = definition.descriptors.authorityCommands[0]!;
const pending = (): ReadonlyArray<RecoveryItem> => {
  const common = {
    createdAt: "2026-10-24T00:00:00.000000Z",
    expiresAt: "2026-10-31T00:00:00.000000Z",
    offers: ["RETRY", "FORGET"] as const,
  };
  return [
    {
      ...common,
      recoveryId: "20000000-0000-4000-8000-000000000001",
      operationId: "20000000-0000-4000-8000-000000000002",
      scope: "CASE",
      act: command.kind,
      caseReference: "SYNTHETIC-REFERENCE",
      expectedRevision: 0,
      targetActorId: null,
    },
    {
      ...common,
      recoveryId: "20000000-0000-4000-8000-000000000003",
      operationId: "20000000-0000-4000-8000-000000000004",
      scope: "AUTHORITY",
      act: authority.kind,
      caseReference: null,
      expectedRevision: null,
      targetActorId: "20000000-0000-4000-8000-000000000005",
    },
  ];
};
afterEach(() => vi.restoreAllMocks());
for (const language of ["en", "lv", "ar"] as const) {
  it(`renders authored case and authority recovery labels in ${language} without executing`, async () => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
    );
    const items = pending();
    vi.spyOn(service, "pending").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: { outcome: { tag: "SUCCEEDED", data: { items, nextCursor: null } } },
    });
    const retry = vi.spyOn(service, "retry");
    render(<RecoveryRequests token="token" definition={definition} onLockChange={vi.fn()} />);
    await userEvent.setup().click(screen.getByRole("button"));
    const rows = await screen.findAllByRole("listitem");
    const catalog: Readonly<Record<string, string>> = catalogs[language];
    for (const [index, descriptor] of [command, authority].entries()) {
      const row = within(rows[index]!);
      expect(row.getByText(catalog[descriptor.labelId]!, { exact: true })).toBeVisible();
      expect(row.queryByText(descriptor.kind, { exact: true })).toBeNull();
      expect(row.getByText(items[index]!.operationId, { exact: true })).toBeVisible();
      expect(items[index]!.act).toBe(descriptor.kind);
    }
    expect(screen.queryByRole("textbox")).toBeNull();
    expect(retry).not.toHaveBeenCalled();
  });
}
