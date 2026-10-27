// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import * as fs from "node:fs/promises";
import type { Page, TestInfo } from "@playwright/test";
import { noHorizontalOverflow } from "../../e2e/layout-support";

vi.mock("node:fs/promises", async (original) => ({
  ...(await original<typeof fs>()),
  writeFile: vi.fn(),
}));
const info = vi.hoisted(() => vi.fn<() => Pick<TestInfo, "outputPath">>());
vi.mock("@playwright/test", () => ({ test: { info } }));
const metrics = {
  clientWidth: 320,
  scrollWidth: 400,
  bodyWidth: 320,
  offenders: [{ kind: "div", className: "safe!<>   name", left: 0, right: 400, width: 400 }],
  scrollable: [],
};
const measured = () => ({ evaluate: vi.fn<Page["evaluate"]>().mockResolvedValue(metrics) });
afterEach(() => {
  vi.resetAllMocks();
});
it("propagates a measurement failure unchanged without reporting", async () => {
  const page = measured();
  const failure = new Error("MEASUREMENT_SENTINEL");
  page.evaluate.mockRejectedValueOnce(failure);
  await expect(noHorizontalOverflow(page, "narrow")).rejects.toBe(failure);
  expect(info).not.toHaveBeenCalled();
  expect(fs.writeFile).not.toHaveBeenCalled();
});
it("uses the existing one-pixel tolerance without report I/O", async () => {
  const page = measured();
  page.evaluate.mockResolvedValueOnce({ ...metrics, scrollWidth: 321 });
  await expect(noHorizontalOverflow(page, "zoom")).resolves.toBeUndefined();
  expect(info).not.toHaveBeenCalled();
  expect(fs.writeFile).not.toHaveBeenCalled();
});
const assertFixedFailure = async (operation: Promise<void>, marker: string) => {
  const failure: unknown = await operation.catch((error: unknown) => error);
  expect(failure).toBeInstanceOf(Error);
  expect(String(failure)).toBe(`Error: ${marker}`);
  expect(failure).not.toHaveProperty("cause");
};
it("keeps the primary marker when per-test path creation fails", async () => {
  const outputPath = vi.fn<TestInfo["outputPath"]>().mockImplementationOnce(() => {
    throw new Error("PATH_SENTINEL");
  });
  info.mockReturnValue({ outputPath });
  await assertFixedFailure(
    noHorizontalOverflow(measured(), "cases"),
    "E2E_LAYOUT_CASES_OVERFLOW_REPORT_UNAVAILABLE",
  );
  expect(outputPath).toHaveBeenCalledWith("layout.json");
  expect(fs.writeFile).not.toHaveBeenCalled();
});
it("keeps the primary marker when writing the private report fails", async () => {
  const outputPath = vi
    .fn<TestInfo["outputPath"]>()
    .mockReturnValue("/synthetic/private/layout.json");
  info.mockReturnValue({ outputPath });
  vi.mocked(fs.writeFile).mockRejectedValueOnce(new Error("WRITE_SENTINEL"));
  await assertFixedFailure(
    noHorizontalOverflow(measured(), "login"),
    "E2E_LAYOUT_LOGIN_OVERFLOW_REPORT_UNAVAILABLE",
  );
  expect(outputPath).toHaveBeenCalledWith("layout.json");
  expect(fs.writeFile).toHaveBeenCalledOnce();
});
