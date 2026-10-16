import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { OperationEditor } from "../src/views/OperationEditor";
import { productMetadata } from "@contracts/product-metadata";
import { wireSchemas } from "@contracts/browser-contract";
import { wireSchemas as extensionSchemas } from "@contracts/extension-fixture";
import type { CommandKind } from "../src/interaction/metadata";
import { current, previewReply } from "./presentation-state.fixtures";
import { response } from "./service-ui.fixtures";
import { generatedWebValue } from "./contract-corpus.fixtures";

vi.mock("@contracts/product-metadata", async (original) => {
  const actual = await original<{ productMetadata: typeof productMetadata }>();
  const metadata = structuredClone(actual.productMetadata);
  const { descriptors } = metadata;
  const { extensionDescriptor, extensionField } = await import("@contracts/extension-fixture");
  return {
    productMetadata: {
      ...metadata,
      descriptors: {
        ...descriptors,
        inputFields: [...descriptors.inputFields, extensionField],
        commands: [...descriptors.commands, extensionDescriptor],
      },
    },
  };
});

const extendReceiptActs = (node: unknown): void => {
  if (typeof node !== "object" || node === null) {
    return;
  }
  for (const [name, child] of Object.entries(node)) {
    if (name === "enum" && Array.isArray(child) && child.includes("CLOSE")) {
      child.push("SYNTHETIC_CORRECTION");
    } else {
      extendReceiptActs(child);
    }
  }
};

const renderExtension = () => {
  render(
    <OperationEditor
      token="token"
      definition={productMetadata}
      current={{
        ...current,
        offers: [
          {
            definition: "SYNTHETIC_CORRECTION",
            caseReference: null,
            expectedRevision: null,
            choices: [],
          },
        ],
      }}
      initialCommand={"SYNTHETIC_CORRECTION" as CommandKind}
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />,
  );
};

it("uses the production case editor, reducer, generated builder and delivery for an added operation, field and correction group", async () => {
  const originals = structuredClone(wireSchemas);
  Object.assign(wireSchemas["case.execute"].request, extensionSchemas["extension.execute"].request);
  Object.assign(wireSchemas["case.preview"].request, extensionSchemas["extension.preview"].request);
  extendReceiptActs(wireSchemas["case.execute"].response);
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockImplementationOnce(() => Promise.resolve(previewReply()))
    .mockImplementationOnce(() =>
      Promise.resolve(
        response("case.execute", "ACCEPTED", generatedWebValue("case.execute").outcome.data),
      ),
    );
  vi.stubGlobal("fetch", fetch);
  try {
    renderExtension();
    const user = userEvent.setup();
    await user.selectOptions(screen.getByRole("combobox", { name: "Action" }), "REPLACE");
    await user.type(screen.getByRole("textbox", { name: "syntheticText" }), "Exact 1.0000 العربية");
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
    await user.click(screen.getByRole("button", { name: "Record changes" }));
    await screen.findByRole("heading", { name: "Recorded change" });
    expect(fetch.mock.calls.map(([url]) => url)).toEqual([
      "/api/case.preview",
      "/api/case.execute",
    ]);
    const preview = fetch.mock.calls[0]?.[1]?.body;
    expect(fetch.mock.calls[1]?.[1]?.body).toBe(preview);
    if (typeof preview !== "string") {
      throw new Error("Missing exact prepared bytes.");
    }
    expect(JSON.parse(preview)).toMatchObject({
      act: {
        kind: "SYNTHETIC_CORRECTION",
        groups: {
          syntheticGroup: { mode: "REPLACE", values: { syntheticText: "Exact 1.0000 العربية" } },
        },
      },
    });
  } finally {
    for (const endpoint of ["case.preview", "case.execute"] as const) {
      Object.assign(wireSchemas[endpoint].request, originals[endpoint].request);
      Object.assign(wireSchemas[endpoint].response, originals[endpoint].response);
    }
  }
});
