import { createContext } from 'react';

/** Selects a trace node from inside the canvas (edge labels, which React Flow does not route clicks for). */
export const TraceSelectContext = createContext<(id: string) => void>(() => {});
