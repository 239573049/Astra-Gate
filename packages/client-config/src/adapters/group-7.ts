import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { AntigravityCliClientAdapter } from './antigravity-cli.js';
import { CommandCodeClientAdapter } from './command-code.js';
import { DimClientAdapter } from './dim.js';
import { FxClientAdapter } from './fx.js';
import { GooseClientAdapter } from './goose.js';
import { MothXClientAdapter } from './mothx.js';
import { OpenCodeClientAdapter, OpenCodeFlavours } from './opencode.js';
import { PenguinHarnessClientAdapter } from './penguin-harness.js';
import { PiClientAdapter, PiFlavours } from './pi.js';

/** Adapter group 7: the OpenCode / Pi families and the simple CLI agents of magpie-parity batch 1. */
export function group7(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new OpenCodeClientAdapter(env, store, OpenCodeFlavours.OpenChamber),
    new OpenCodeClientAdapter(env, store, OpenCodeFlavours.DevEcoCode),
    new PiClientAdapter(env, store, PiFlavours.StepCode),
    new PiClientAdapter(env, store, PiFlavours.Aside),
    new PiClientAdapter(env, store, PiFlavours.OmO),
    new PiClientAdapter(env, store, PiFlavours.PrimeAgent),
    new PiClientAdapter(env, store, PiFlavours.Mu),
    new MothXClientAdapter(env, store),
    new PenguinHarnessClientAdapter(env, store),
    new AntigravityCliClientAdapter(env, store),
    new DimClientAdapter(env, store),
    new CommandCodeClientAdapter(env, store),
    new FxClientAdapter(env, store),
    new GooseClientAdapter(env, store),
  ];
}
