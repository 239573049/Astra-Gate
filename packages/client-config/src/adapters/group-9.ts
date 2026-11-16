import type { IClientAdapter, IClientConfigStateStore } from '../contracts.js';
import type { ClientEnvironment } from '../env.js';
import { ReasonixClientAdapter } from './reasonix.js';
import { MorphClientAdapter } from './morph.js';
import { JcodeClientAdapter } from './jcode.js';
import { VibeClientAdapter } from './vibe.js';
import { EmpryoClientAdapter } from './empryo.js';
import { NanobotClientAdapter } from './nanobot.js';
import { MiniQClientAdapter } from './miniq.js';
import { PaimonClientAdapter } from './paimon.js';
import { ClineClientAdapter } from './cline.js';
import { GrokAppClientAdapter } from './grok-app.js';
import { AtomCodeClientAdapter } from './atomcode.js';
import { SnowClientAdapter } from './snow.js';
import { AnteClientAdapter } from './ante.js';
import { CraftAgentsClientAdapter } from './craft-agents.js';
import { TuanjieCoworkClientAdapter } from './tuanjie-cowork.js';

/** Adapter group 9: the file-backed CLI and desktop coding agents of magpie-parity batch 3 (the C# `FileClientAdapter` family). */
export function group9(env: ClientEnvironment, store: IClientConfigStateStore): IClientAdapter[] {
  return [
    new ReasonixClientAdapter(env, store),
    new MorphClientAdapter(env, store),
    new JcodeClientAdapter(env, store),
    new VibeClientAdapter(env, store),
    new EmpryoClientAdapter(env, store),
    new NanobotClientAdapter(env, store),
    new MiniQClientAdapter(env, store),
    new PaimonClientAdapter(env, store),
    new ClineClientAdapter(env, store),
    new GrokAppClientAdapter(env, store),
    new AtomCodeClientAdapter(env, store),
    new SnowClientAdapter(env, store),
    new AnteClientAdapter(env, store),
    new CraftAgentsClientAdapter(env, store),
    new TuanjieCoworkClientAdapter(env, store),
  ];
}
