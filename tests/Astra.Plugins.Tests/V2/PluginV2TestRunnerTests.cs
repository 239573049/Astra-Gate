using Astra.Plugins.V2;

namespace Astra.Plugins.Tests.V2;

/// <summary>astra plugin test: a plugin's own tests run in the sandbox against the real host calls (design/plugin-v2 06 §4).</summary>
public class PluginV2TestRunnerTests
{
    private static string Plugin(params (string Path, string Text)[] files)
    {
        var dir = Directory.CreateTempSubdirectory("astra-v2-test-").FullName;
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), """
            { "manifestVersion": 2, "id": "acme.tested", "version": "1.0.0", "name": "T", "main": "main.js", "gateway": "gateway.js",
              "permissions": ["ui", "storage", "gateway.request:reject"],
              "contributes": { "ui.pages": [{ "id": "home", "title": "H" }], "commands": [{ "id": "double", "title": "D" }],
                               "gateway.hooks": [{ "id": "guard", "on": ["request"] }] } }
            """);
        File.WriteAllText(Path.Combine(dir, "main.js"), """
            import { definePlugin, commands, storage, secrets, ui } from 'astra';
            export const home = ui.defineView({
              init: async () => ({ seen: (await storage.get('seen')) ?? 0 }),
              render: (s) => ui.Stack({}, [ui.MetricCard({ label: 'Seen', value: s.seen }), ui.Button({ label: '+', onPress: 'inc' })]),
              actions: {
                async inc(s, _a, ctx) { if (await ctx.confirm('sure?')) { s.seen += 1; await storage.set('seen', s.seen); } },
                async leak() { await secrets.get('k'); },
              },
            });
            export default definePlugin({ activate(ctx) { ctx.subscriptions.push(commands.register('double', (a) => a.x * 2)); } });
            """);
        File.WriteAllText(Path.Combine(dir, "gateway.js"), """
            import { gateway } from 'astra';
            export default gateway.defineHooks({ guard: { onRequest(req, ctx) { if (JSON.stringify(req).includes('secret')) ctx.reject(400, 'no'); return req; } } });
            """);
        foreach (var (path, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
            File.WriteAllText(Path.Combine(dir, path), text);
        }
        return dir;
    }

    [Fact]
    public async Task Runs_the_tests_in_the_sandbox_and_reports_each()
    {
        var dir = Plugin(("test/home.test.js", """
            import { describe, it, expect } from 'vitest';
            import { mount, runCommand, runHook } from '@astragate/plugin-sdk/testing';
            import plugin, { home } from '../main.js';
            import hooks from '../gateway.js';

            describe('home', () => {
              it('reads storage and confirms before counting', async () => {
                const v = await mount(home, { storage: { seen: 2 }, confirm: true });
                expect(v.find('MetricCard').props.value).toBe(2);
                await v.press('inc');
                expect(v.state.seen).toBe(3);
                expect(await v.storage.get('seen')).toBe(3);
                expect(v.host.asked).toContainEqual({ type: 'confirm', title: 'sure?' });
              });
              it('fails on purpose', () => { expect(1).toBe(2); });
              it('is refused a permission it lacks', async () => {
                const v = await mount(home);
                await expect(v.press('leak')).rejects.toThrow('secrets');
              });
            });
            it('runs a command and a hook', async () => {
              expect(await runCommand(plugin, 'double', { x: 21 })).toBe(42);
              expect((await runHook(hooks, 'guard', 'request', { t: 'a secret' })).rejected).toEqual({ status: 400, message: 'no' });
            });
            """));

        var results = await PluginV2TestRunner.RunAsync(dir, "0.15.0");

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal("test/home.test.js", r.File));
        Assert.Equal("passed", results.Single(r => r.Name == "home › reads storage and confirms before counting").Status);
        var failed = results.Single(r => r.Name == "home › fails on purpose");
        Assert.Equal("failed", failed.Status);
        Assert.Contains("expected 1 to be 2", failed.Error);
        Assert.Equal("passed", results.Single(r => r.Name == "home › is refused a permission it lacks").Status);
        Assert.Equal("passed", results.Single(r => r.Name == "runs a command and a hook").Status);
    }

    [Fact]
    public async Task A_file_that_does_not_load_is_one_failure()
    {
        var dir = Plugin(("test/broken.test.js", "import { nope } from 'lodash';"));
        var r = Assert.Single(await PluginV2TestRunner.RunAsync(dir, "0.15.0"));
        Assert.Equal("failed", r.Status);
        Assert.Equal("(load)", r.Name);
    }

    [Fact]
    public async Task Providers_and_clients_run_in_the_sandbox_too()
    {
        var dir = Directory.CreateTempSubdirectory("astra-v2-test-").FullName;
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), """
            { "manifestVersion": 2, "id": "acme.cloud", "version": "1.0.0", "name": "C", "main": "main.js",
              "permissions": ["providers", "clients", "net:https://api.acme.example"],
              "contributes": { "providers": [{ "id": "acme", "protocols": ["chat"] }],
                               "clients": [{ "id": "acme-cli", "config": "~/.acme.json", "model": "model" }] } }
            """);
        File.WriteAllText(Path.Combine(dir, "main.js"), """
            import { definePlugin, providers, clients } from 'astra';
            export default definePlugin({ activate() {
              providers.register('acme', { prepare: async (req, account) => ({ url: 'https://api.acme.example/v1', headers: { authorization: 'Bearer ' + (await account.secret()) } }) });
              clients.register('acme-cli', { connect: (input) => ({ url: input.gateway.v1 }) });
            } });
            """);
        Directory.CreateDirectory(Path.Combine(dir, "test"));
        File.WriteAllText(Path.Combine(dir, "test", "p.test.js"), """
            import { it, expect } from 'vitest';
            import { runProvider, runClient } from '@astragate/plugin-sdk/testing';
            import plugin from '../main.js';
            it('prepares with the secret', async () => {
              const r = await runProvider(plugin, 'acme', 'prepare', { protocol: 'chat' }, { key: 'k', secret: 's3' });
              expect(r.headers.authorization).toBe('Bearer s3');
            });
            it('connects', async () => expect(await runClient(plugin, 'acme-cli', { gateway: { v1: 'http://x/v1' } })).toEqual({ url: 'http://x/v1' }));
            """);
        var results = await PluginV2TestRunner.RunAsync(dir, "0.15.0");
        Assert.All(results, r => Assert.True(r.Status == "passed", r.Name + ": " + r.Error));
        Assert.Equal(2, results.Count);
    }
}
