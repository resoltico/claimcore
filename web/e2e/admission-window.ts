export const waitForLoginPermit = async (
  probe: (remainingMilliseconds: number) => Promise<number>,
  pause: (milliseconds: number) => Promise<void>,
  now: () => number = () => performance.now(),
): Promise<void> => {
  const deadline = now() + 61_000;
  while (true) {
    const budget = deadline - now();
    if (budget <= 0) {
      break;
    }
    const status = await probe(budget);
    if (now() >= deadline) {
      break;
    }
    if (status === 302) {
      return;
    }
    if (status !== 429) {
      throw new Error(`E2E_RATE_RECOVERY_STATUS_${status}`);
    }
    const remaining = deadline - now();
    if (remaining > 0) {
      await pause(Math.min(1_000, remaining));
    }
  }
  throw new Error("E2E_RATE_RECOVERY_TIMEOUT");
};
