import { createContext, ReactNode, useContext, useEffect, useState } from 'react';
import { useRuns } from '../api/hooks';

interface Ctx {
  runId: string | undefined;
  setRunId: (id: string) => void;
}
const RunCtx = createContext<Ctx>({ runId: undefined, setRunId: () => {} });

// Tracks the "selected run" across screens; defaults to the latest run.
export function RunProvider({ children }: { children: ReactNode }) {
  const { data: runs } = useRuns();
  const [runId, setRunId] = useState<string | undefined>();
  useEffect(() => {
    if (!runId && runs && runs.length) setRunId(runs[0].id);
  }, [runs, runId]);
  return <RunCtx.Provider value={{ runId, setRunId }}>{children}</RunCtx.Provider>;
}

export const useSelectedRun = () => useContext(RunCtx);
