import { useWorkspaceFind, workspaceFindId } from "../../src/shell/useWorkspaceFind";
import { Button } from "../../src/ui/Button";
import { Dashboard } from "../../src/shell/Dashboard";
import { navigate } from "../../src/routing/navigation";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { act, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { expect, it, vi } from "vitest";
import { service } from "../../src/api/service";
import { FindCase } from "../../src/features/work/FindCase";
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

it("opens Find from a malformed case read and leaves other input refusals alone", async () => {
  stubDashboardContext();
  const malformed = "/cases/reference/CC-0000-0000-0000-0000-01";
  const unrelated = "/cases/reference/OTHER-INPUT";
  mockEndpoint("case.get").mockImplementation((_credential, { target }) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "INPUT",
            advice: {
              message: {
                messageId: "diagnostic.INVALID_INPUT",
                arguments: [{ key: "issueCount", value: { kind: "COUNT", value: 1 } }],
              },
              action: "CORRECT_INPUT",
            },
            issues: [
              target.kind === "REFERENCE" && malformed.endsWith(target.caseReference)
                ? {
                    input: "field:caseReference",
                    row: null,
                    message: { messageId: "diagnostic.REFERENCE_FORMAT", arguments: [] },
                  }
                : {
                    input: "field:caseReference",
                    row: null,
                    message: { messageId: "diagnostic.INVALID_INPUT", arguments: [] },
                  },
            ],
          },
        },
      },
    }),
  );
  const user = userEvent.setup();
  navigate(malformed);
  render(<Dashboard />);
  const main = within(screen.getByRole("main"));
  expect(await main.findByRole("alert")).toHaveTextContent(
    /^Check the case reference and its hyphens/u,
  );
  const recovery = main.getByRole("button", { name: "Find a case" });
  expect(main.queryByRole("button", { name: "Try again" })).toBeNull();

  await user.click(recovery);
  const dialog = screen.getByRole("dialog", { name: "Find a case" });
  expect(within(dialog).getByRole("combobox")).toHaveFocus();
  expect(within(dialog).getByRole("combobox")).toHaveValue("");
  await user.keyboard("{Escape}");
  await waitFor(() => {
    expect(recovery).toHaveFocus();
  });

  act(() => {
    navigate(unrelated);
  });
  expect(await main.findByRole("alert")).toHaveTextContent("Fields to check: 1.");
  expect(main.queryByRole("button", { name: "Find a case" })).toBeNull();
  expect(screen.queryByRole("dialog")).toBeNull();
});

it("returns to the shell Find button when the inline trigger disappears", async () => {
  const FindWorkspace = ({ inline }: { inline: boolean }) => {
    const find = useWorkspaceFind("owner", true, false);
    return (
      <>
        <Button id={workspaceFindId(false)}>Search</Button>
        {inline ? (
          <Button
            id="inline-find"
            onPress={() => {
              find.onFind?.("inline-find");
            }}
          >
            Correct selector
          </Button>
        ) : null}
        <FindCase open={find.open} setOpen={find.dismiss} returnFocusId={find.returnFocusId} />
      </>
    );
  };
  const user = userEvent.setup();
  const view = render(<FindWorkspace inline />);
  await user.click(screen.getByRole("button", { name: "Correct selector" }));
  expect(screen.getByRole("dialog", { name: "Find a case" })).toBeVisible();
  view.rerender(<FindWorkspace inline={false} />);
  await user.keyboard("{Escape}");
  await waitFor(() => {
    expect(screen.getByRole("button", { name: "Search" })).toHaveFocus();
  });
});
