import { act, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { expect, it, vi } from "vitest";
import { service } from "../../src/api/service";
import { FindCase } from "../../src/views/FindCase";
import { render, screen, waitFor } from "../support/presentation-test-support";

it("supersedes repeated Enter and drops a held refusal after closing Find", async () => {
  const first = Promise.withResolvers<Awaited<ReturnType<typeof service.get>>>();
  const second = Promise.withResolvers<Awaited<ReturnType<typeof service.get>>>();
  const get = vi
    .spyOn(service, "get")
    .mockReturnValueOnce(first.promise)
    .mockReturnValueOnce(second.promise);
  const refusal = {
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  } as const;
  const Find = () => {
    const [open, setOpen] = useState(true);
    return <FindCase open={open} setOpen={setOpen} returnFocusId="find-trigger" />;
  };
  try {
    render(<Find />);
    const user = userEvent.setup();
    const dialog = await screen.findByRole("dialog", { name: "Find a case" });
    await user.type(within(dialog).getByRole("combobox"), "CLM-0012");
    await user.keyboard("{Enter}");
    await waitFor(() => {
      expect(get).toHaveBeenCalledOnce();
    });
    await user.keyboard("{Enter}");
    await waitFor(() => {
      expect(get).toHaveBeenCalledTimes(2);
    });
    expect(get.mock.calls[0]?.[2]?.aborted).toBe(true);
    expect(get.mock.calls[1]?.[2]?.aborted).toBe(false);
    await act(() =>
      Promise.resolve().then(() => {
        first.resolve(refusal);
      }),
    );
    expect(within(dialog).queryByRole("alert")).toBeNull();

    await user.click(within(dialog).getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog", { name: "Find a case" })).toBeNull();
    expect(get.mock.calls[1]?.[2]?.aborted).toBe(true);
    await act(() =>
      Promise.resolve().then(() => {
        second.resolve(refusal);
      }),
    );
    expect(screen.queryByRole("alert")).toBeNull();
  } finally {
    get.mockRestore();
  }
});
