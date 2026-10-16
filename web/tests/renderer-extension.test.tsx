import { useState } from "react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { ProtocolFields } from "../src/components/ProtocolFields";
import { formValue, type FormValues } from "../src/protocol/forms";
import { sendHttp } from "../src/api/transport";
import { responseBytes } from "../src/api/responseBytes";
import {
  wireSchemas,
  buildRequest,
  decodeResponse,
  correlateReply,
} from "@contracts/extension-fixture";

const document = wireSchemas["extension.execute"].request;
const operationId = "10000000-0000-4000-8000-000000000001";
const requestValues = { operationId, caseReference: "EXTENSION", expectedRevision: "0" };
const deliverExtension = async (execute: boolean, values: FormValues, body: string | null) => {
  const endpoint = execute ? "extension.execute" : "extension.preview";
  const request = buildRequest(endpoint, formValue(document, document, "", values));
  const bytes = body ?? JSON.stringify(request);
  const { signal } = new AbortController();
  const response = await sendHttp(
    { path: `/api/${endpoint}`, method: "POST" },
    { "content-type": "application/json" },
    bytes,
    signal,
  );
  const result = decodeResponse(endpoint, await responseBytes(response, 16777216, signal));
  if (!correlateReply(endpoint, request, result)) {
    throw new Error("Synthetic reply mismatch.");
  }
  return bytes;
};
const Extension = () => {
  const [values, setValues] = useState<FormValues>(requestValues);
  const [body, setBody] = useState<string | null>(null);
  const [reviewed, setReviewed] = useState(false);
  const [accepted, setAccepted] = useState(false);
  const send = async (execute: boolean) => {
    setBody(await deliverExtension(execute, values, body));
    if (execute) {
      setAccepted(true);
    } else {
      setReviewed(true);
    }
  };
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        void send(false);
      }}
    >
      <ProtocolFields
        document={document}
        node={document}
        path=""
        values={values}
        disabled={body !== null}
        onChange={(path, value) => {
          setValues({ ...values, [path]: value });
        }}
      />
      <button type="submit">Review synthetic operation</button>
      {reviewed ? (
        <button type="button" onClick={() => void send(true)}>
          Execute synthetic operation
        </button>
      ) : null}
      {accepted ? <p role="status">Synthetic operation accepted</p> : null}
    </form>
  );
};
it("renders a generated additional operation, field and correction group through the real transport seam", async () => {
  const sent: string[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((url, init) => {
    if (typeof url !== "string" || typeof init?.body !== "string") {
      throw new Error("Synthetic exact request is missing.");
    }
    const request = buildRequest("extension.execute", JSON.parse(init.body));
    sent.push(init.body);
    const identity = {
      caseReference: request.caseReference,
      revision: request.expectedRevision + 1,
    };
    const execute = url.endsWith("extension.execute");
    const outcome = execute
      ? {
          tag: "ACCEPTED",
          data: { ...identity, operationId, scope: "CASE", act: request.act.kind },
        }
      : { tag: "REVIEWED", data: { review: { after: identity } } };
    return Promise.resolve(
      new Response(
        JSON.stringify({
          endpoint: execute ? "extension.execute" : "extension.preview",
          operationId,
          outcome,
        }),
        { headers: { "content-type": "application/json" } },
      ),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  render(<Extension />);
  await user.selectOptions(screen.getByRole("combobox", { name: "mode" }), "REPLACE");
  await user.type(screen.getByRole("textbox", { name: "syntheticText" }), "Exact 1.0000 العربية");
  await user.click(screen.getByRole("button", { name: "Review synthetic operation" }));
  await user.click(await screen.findByRole("button", { name: "Execute synthetic operation" }));
  await screen.findByRole("status");
  expect(sent).toHaveLength(2);
  expect(sent[1]).toBe(sent[0]);
  expect(JSON.parse(sent[0]!)).toMatchObject({
    act: {
      kind: "SYNTHETIC_CORRECTION",
      groups: {
        syntheticGroup: { mode: "REPLACE", values: { syntheticText: "Exact 1.0000 العربية" } },
      },
    },
  });
});
