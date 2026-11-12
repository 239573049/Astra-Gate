export { jsx, jsxs, Fragment } from './index';
import type { UiNode } from './index';
export namespace JSX {
  type Element = UiNode;
  interface IntrinsicElements {
    [name: string]: Record<string, unknown>;
  }
}
