import type { ApiResult } from "../protocol/types";
import { localNotice } from "../protocol/notices";

const maximumConcurrentReads = 2;

type Release = () => void;
type WaitingRead = {
  signal: AbortSignal;
  resolve: (release: Release | null) => void;
  cancel: () => void;
};

/** A document bounds simultaneous read responses; writes retain their existing handoff path. */
export class ReadAdmission {
  private active = 0;
  private readonly waiting = new Set<WaitingRead>();

  private start(signal: AbortSignal): Release {
    this.active += 1;
    let held = true;
    const release = () => {
      if (held) {
        held = false;
        signal.removeEventListener("abort", release);
        this.active -= 1;
        this.advance();
      }
    };
    signal.addEventListener("abort", release, { once: true });
    return release;
  }

  private advance(): void {
    for (const next of this.waiting) {
      if (this.active >= maximumConcurrentReads) {
        break;
      }
      this.waiting.delete(next);
      next.signal.removeEventListener("abort", next.cancel);
      next.resolve(next.signal.aborted ? null : this.start(next.signal));
    }
  }

  private wait(signal: AbortSignal): Promise<Release | null> {
    return new Promise((resolve) => {
      const next: WaitingRead = {
        signal,
        resolve,
        cancel: () => {
          this.waiting.delete(next);
          resolve(null);
        },
      };
      this.waiting.add(next);
      signal.addEventListener("abort", next.cancel, { once: true });
    });
  }

  private async execute<T>(
    signal: AbortSignal,
    release: Release | null,
    work: () => Promise<ApiResult<T>>,
  ): Promise<ApiResult<T>> {
    if (release === null || signal.aborted) {
      release?.();
      return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
    }
    try {
      return await work();
    } finally {
      release();
    }
  }
  run<T>(signal: AbortSignal, work: () => Promise<ApiResult<T>>): Promise<ApiResult<T>> {
    if (signal.aborted) {
      return this.execute(signal, null, work);
    }
    if (this.active < maximumConcurrentReads) {
      return this.execute(signal, this.start(signal), work);
    }
    return this.wait(signal).then((release) => this.execute(signal, release, work));
  }
}
