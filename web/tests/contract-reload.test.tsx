import { beforeEach, it, expect, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { CaseList } from "../src/views/CaseList";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("contract mismatch offers explicit reload and never automatically redispatches", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(
    new Response(
      JSON.stringify({ hostFailure: { code: "CONTRACT_MISMATCH", phase: "NOT_STARTED" } }),
      { status: 409, headers: { "content-type": "application/json" } },
    ),
  );
  render(<CaseList token="csrf" onSelect={vi.fn()} onOpen={vi.fn()} />);
  expect(await screen.findByRole("button", { name: "Reload page" })).toBeVisible();
  expect(fetch).toHaveBeenCalledOnce();
  expect(vi.mocked(fetch).mock.calls.map(([url]) => url)).toEqual(["/api/case.list"]);
});
