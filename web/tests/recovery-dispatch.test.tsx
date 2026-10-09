import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "./presentation-test-support";
import { RecoveryView } from "../src/views/RecoveryView";
import { operationId, preparation } from "./v3-ui.fixtures";
import { inspection, list } from "./v3-recovery.fixtures";

const exportReply = (success: boolean): Response =>
  new Response("{}", {
    headers: success
      ? {
          "content-type": "application/vnd.claimcore.recovery+json",
          "content-disposition": `attachment; filename=claimcore-recovery-${operationId}.json; filename*=UTF-8''claimcore-recovery-${operationId}.json`,
        }
      : { "content-type": "application/json" },
  });

it.each([true, false])(
  "keeps closed inspection controls locked until export settles (%s) [CC-REC-001]",
  async (success) => {
    const user = userEvent.setup();
    let finish: (reply: Response) => void = () => undefined;
    const pending = new Promise<Response>((resolve) => {
      finish = resolve;
    });
    const fetch = vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValueOnce(list())
      .mockResolvedValueOnce(inspection())
      .mockReturnValueOnce(pending);
    vi.stubGlobal("fetch", fetch);
    const createObjectURL = vi.fn(() => "blob:synthetic");
    Object.assign(URL, {
      createObjectURL,
      revokeObjectURL: vi.fn(),
    });
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    const lock = vi.fn();
    const recover = vi.fn();
    render(<RecoveryView token="token" onRecovery={recover} onMutationLockChange={lock} />);
    await user.click(await screen.findByRole("button", { name: "Inspect" }));
    await user.click(screen.getByRole("button", { name: "Export recovery envelope" }));
    await user.click(await screen.findByRole("button", { name: "Confirm export" }));
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(3);
    });
    await user.click(screen.getByRole("button", { name: "Close inspection" }));
    const inspect = screen.getByRole("button", { name: "Inspect" });
    const importing = screen.getByRole("button", { name: "Import recovery envelope" });
    expect(inspect).toBeDisabled();
    expect(importing).toBeDisabled();
    await user.click(inspect);
    await user.click(importing);
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(fetch.mock.calls[2]?.[1]?.body).toBe(
      JSON.stringify({ operationId, requestSha256: preparation.summary.requestSha256 }),
    );
    expect(lock.mock.calls).toEqual([[true]]);
    finish(exportReply(success));
    await waitFor(() => {
      expect(inspect).not.toBeDisabled();
    });
    expect(importing).not.toBeDisabled();
    expect(lock.mock.calls).toEqual([[true], [false]]);
    expect(screen.queryByRole("dialog", { name: "Recovery details" })).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(recover).toHaveBeenCalledTimes(success ? 0 : 1);
    expect(createObjectURL).toHaveBeenCalledTimes(success ? 1 : 0);
  },
);
