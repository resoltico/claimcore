import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { ReceiptFields } from "../../src/components/ReceiptFields";
import { OperationLookup } from "../../src/views/OperationLookup";
import { wireReply } from "../support/contract-corpus.fixtures";
import { definition, response } from "../support/service-ui.fixtures";
it.each([
  ["WITHHELD", "The recorded fields are withheld under your current access rights."],
  ["ERASED", "Erased — only the case reference and revision remain."],
] as const)("presents %s distinctly without recorded fields", (tag, message) => {
  const source = wireReply("operation.execute").outcome;
  if (source.kind !== "ACCEPTED" || source.data.subject.kind !== "CASE") {
    throw new Error("Case receipt fixture absent.");
  }
  const view = render(
    <ReceiptFields
      subject={{
        ...source.data.subject,
        snapshot: {
          kind: tag,
        },
      }}
      fields={definition.catalog.fields}
      context="Synthetic receipt"
    />,
  );
  expect(screen.getByText(message)).toBeVisible();
  expect(view.container.querySelector(".case-fields")).toBeNull();
});
it("observes authority receipts as attributed metadata without assuming a case snapshot", async () => {
  const source = wireReply("operation.execute", "AUTHORITY").outcome;
  if (source.kind !== "ACCEPTED" || source.data.subject.kind !== "AUTHORITY") {
    throw new Error("Authority receipt fixture absent.");
  }
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValueOnce(response("operation.observe", "SUCCEEDED", source.data)),
  );
  render(<OperationLookup definition={definition} initialOperationId={source.data.operationId} />);
  await userEvent.setup().click(
    screen.getByRole("button", {
      name: "Look up recorded result",
    }),
  );
  expect(
    await screen.findByText(
      `Authority act Provision owner for actor ${source.data.subject.targetActorId}.`,
    ),
  ).toBeVisible();
  expect(document.querySelector(".case-fields")).toBeNull();
});
