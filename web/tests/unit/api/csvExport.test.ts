import { ReadableStream } from "node:stream/web";
import { afterEach, expect, it, vi } from "vitest";
import { requestCsvExport, saveCsvDownload } from "../../../src/api/csvExport";
import { csvExportTransport, type ExportRequest } from "@contracts/client";
import type { OwnerScope } from "../../../src/protocol/ownerScope";

const owner: OwnerScope = {
  serviceOrigin: location.origin,
  installationId: "10000000-0000-4000-8000-000000000001",
  actorId: "10000000-0000-4000-8000-000000000002",
  sessionEpoch: 1,
};
const request: ExportRequest = { query: "case.list", selection: { view: "MINE" } };
const text = "caseReference\r\n'=danger\r\n";
const bytes = new TextEncoder().encode(text);
const headers = () => ({
  "Content-Type": "text/csv; charset=utf-8",
  "Content-Length": String(bytes.length),
  "Content-Encoding": "identity",
  "Content-Disposition": `attachment; filename="${csvExportTransport.filenames["case.list"]}"`,
  "ClaimCore-Installation": owner.installationId,
});
const response = (override: Record<string, string> = {}) =>
  new Response(bytes, { status: 200, headers: { ...headers(), ...override } });
afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
});
it("admits only a complete identity-encoded file under the original owner", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response()));
  const result = await requestCsvExport(request, "csrf", owner, () => owner);
  expect(result).toMatchObject({
    kind: "outcome",
    value: { kind: "DOWNLOAD", document: { bytes, filename: "claimcore-cases.csv" } },
  });
  expect(fetch).toHaveBeenCalledWith(
    "/api/export",
    expect.objectContaining({
      body: JSON.stringify(request),
    }),
  );
  expect(
    new Headers(vi.mocked(fetch).mock.calls[0]?.[1]?.headers).get("ClaimCore-Installation"),
  ).toBe(owner.installationId);
});
it.each([
  { "Content-Length": "" },
  { "Content-Length": "01" },
  { "Content-Length": String(bytes.length + 1) },
  { "Content-Length": "99999999999999999" },
  { "Content-Encoding": "gzip" },
  { "Content-Type": "text/plain" },
  { "Content-Disposition": 'attachment; filename="private.csv"' },
  { "ClaimCore-Installation": "10000000-0000-4000-8000-000000000099" },
])("refuses invalid or incomplete CSV metadata/body: %j", async (override) => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response(override)));
  expect((await requestCsvExport(request, "csrf", owner, () => owner)).kind).toBe(
    "deliveryFailure",
  );
});
it("refuses a missing length and a cleanly truncated body", async () => {
  const missing = response();
  missing.headers.delete("Content-Length");
  const truncated = new Response(bytes.slice(0, -1), { status: 200, headers: headers() });
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(missing).mockResolvedValueOnce(truncated));
  expect((await requestCsvExport(request, "csrf", owner, () => owner)).kind).toBe(
    "deliveryFailure",
  );
  expect((await requestCsvExport(request, "csrf", owner, () => owner)).kind).toBe(
    "deliveryFailure",
  );
});
it("retains typed late query failures instead of exposing an earlier successful subset", async () => {
  const failure = {
    outcome: {
      kind: "FAILED",
      fault: {
        code: "CORE_STORE_UNAVAILABLE",
        messageId: "fault.CORE_STORE_UNAVAILABLE",
        action: "RETRY_SAFE",
      },
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(JSON.stringify(failure), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    ),
  );
  expect(await requestCsvExport(request, "csrf", owner, () => owner)).toMatchObject({
    kind: "outcome",
    value: { kind: "FAILED", reply: failure },
  });
});
it("checks owner again only after the complete response has arrived", async () => {
  let current: OwnerScope | null = owner;
  let finish: ((value: Uint8Array) => void) | undefined;
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      finish = (value) => {
        controller.enqueue(value);
        controller.close();
      };
    },
  });
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(new Response(stream as BodyInit, { status: 200, headers: headers() })),
  );
  const pending = requestCsvExport(request, "csrf", owner, () => current);
  await Promise.resolve();
  current = { ...owner, sessionEpoch: 2 };
  finish!(bytes);
  expect((await pending).kind).toBe("deliveryFailure");
});
it("constructs a Blob only for the current admitted owner and releases its object URL", () => {
  vi.useFakeTimers();
  const create = vi.fn().mockReturnValue("blob:synthetic");
  const revoke = vi.fn();
  vi.stubGlobal(
    "URL",
    Object.assign(class extends URL {}, { createObjectURL: create, revokeObjectURL: revoke }),
  );
  const clicked = vi
    .spyOn(HTMLAnchorElement.prototype, "click")
    .mockImplementation(() => undefined);
  const release = saveCsvDownload({ bytes, filename: "claimcore-cases.csv" }, owner, () => owner);
  expect(create).toHaveBeenCalledOnce();
  expect(clicked).toHaveBeenCalledOnce();
  vi.runAllTimers();
  release();
  expect(revoke).toHaveBeenCalledTimes(1);
  saveCsvDownload({ bytes, filename: "claimcore-cases.csv" }, owner, () => null);
  expect(create).toHaveBeenCalledTimes(1);
});
