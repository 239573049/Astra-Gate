import { createContext, useContext } from 'react';

import type { PluginV2Contributions } from '../api/types';

/**
 * What the running v2 plugins add to the console (pages, slots, menu items, commands), provided once by the app shell
 * so host pages read it without a request of their own. Outside the shell (tests, the tray window) there is none, and
 * slots render nothing.
 */
export const ContributionsContext = createContext<PluginV2Contributions | undefined>(undefined);

export const useContributions = () => useContext(ContributionsContext);
