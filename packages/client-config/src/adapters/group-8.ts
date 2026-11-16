import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { AirClientAdapter, AirFlavours } from './air.js';
import { CodeBuddyClientAdapter } from './codebuddy.js';
import { CopilotJetBrainsClientAdapter } from './copilot-jetbrains.js';
import { ObsidianClientAdapter } from './obsidian.js';
import { OpenPencilClientAdapter } from './openpencil.js';
import { PencilClientAdapter } from './pencil.js';
import { QoderClientAdapter, QoderFlavours } from './qoder.js';
import { RaycastClientAdapter } from './raycast.js';
import { T3CodeClientAdapter } from './t3code.js';
import { ZooCodeClientAdapter } from './zoo-code.js';

/** Adapter group 8: the IDEs, editors and their AI extensions of magpie-parity batch 5 (Zoo Code, Air, Copilot for JetBrains, CodeBuddy, Qoder, T3 Code, Pencil, OpenPencil, Raycast, Obsidian). */
export function group8(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new ZooCodeClientAdapter(env, store),
    new AirClientAdapter(env, store, AirFlavours.App),
    new AirClientAdapter(env, store, AirFlavours.Plugin),
    new CopilotJetBrainsClientAdapter(env, store),
    new CodeBuddyClientAdapter(env, store),
    new QoderClientAdapter(env, store, QoderFlavours.Qoder),
    new QoderClientAdapter(env, store, QoderFlavours.QoderCn),
    new T3CodeClientAdapter(env, store),
    new PencilClientAdapter(env, store),
    new OpenPencilClientAdapter(env, store),
    new RaycastClientAdapter(env, store),
    new ObsidianClientAdapter(env, store),
  ];
}
