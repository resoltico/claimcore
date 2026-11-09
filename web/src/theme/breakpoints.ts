// Shell viewport thresholds. Components adapt to their containers.
export const breakpoints = { compact: 40, wide: 64 } as const;
export const compactQuery = `(width < ${breakpoints.compact}rem)`;
