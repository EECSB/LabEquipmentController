//
//AI settings (UI-SPEC §5, The AI assistant): GDS Viewer's window over the server's models, and the
//MCP server card with a real `lec mcp` on the other end of it. Nothing leaves this machine, and the
//keys are made up.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, openSettings, settle } = require('./helpers');
const { startInstrument } = require('./instrument');
const { spawn } = require('node:child_process');
const net = require('node:net');
const path = require('node:path');

async function openAiSettings(page) {
    const menu = await openSettings(page);
    await menu.getByRole('button', { name: /AI settings/ }).click();
    const window = page.locator('dialog.tool[open][aria-label="AI settings"]');
    await expect(window).toBeVisible();
    await settle(page);
    return window;
}

///Every model the test made goes again, since the server is the worker's and outlives the test.
async function forgetNew(request, before) {
    const now = await (await request.get('/api/ai')).json();
    for (const c of now.connections)
        if (!before.connections.some((b) => b.id === c.id))
            await request.delete(`/api/ai/connections/${c.id}`);
    if (before.selectedId) await request.post(`/api/ai/connections/${before.selectedId}/select`);
}

let before;

test.beforeEach(async ({ page, request }) => {
    await page.route((url) => !/^(localhost|127\.0\.0\.1)$/.test(url.hostname), (route) => route.abort());
    before = await (await request.get('/api/ai')).json();
    await freshBench(page, request);
});

test.afterEach(async ({ request }) => {
    await forgetNew(request, before);
});

///Adds a model through the form, as a person does.
async function addModel(window, name, key = 'e2e-never-spent') {
    await window.locator('#aiModelPickerChevron').click();
    await window.locator('#aiModelAdd').click();
    await window.locator('#aiModelPickerName').fill(name);
    await window.locator('#aiModelPickerName').press('Enter');
    await window.locator('#aiProviderAnthropic').click();
    await window.locator('#aiModelKey').fill(key);
    await window.locator('#aiModelSave').click();
    await expect(window.locator('#aiModelPickerFace')).toContainText(name);
}

test.describe('the window', () => {
    test('is a window with the MCP server, the models, speech and rounds', async ({ page }) => {
        const window = await openAiSettings(page);

        await expect(window.locator('.aiSettingsCardTitle').first()).toContainText('MCP server');
        await expect(window.locator('.aiSettingsCard').nth(1)).toContainText('Models');
        await expect(window.locator('#aiKeysKept')).toContainText('One key, shared.');
        await expect(window.locator('.aiSpeechRow')).toContainText('Speech to text');
        await expect(window.locator('.aiRoundsRow')).toContainText('Tool rounds before asking');

        //Not modal: the bench behind it answers.
        const reachable = await page.evaluate(() => {
            const gear = document.querySelector('.titlebar .ghost').getBoundingClientRect();
            const hit = document.elementFromPoint(gear.left + gear.width / 2, gear.top + gear.height / 2);
            return hit !== null && hit.closest('.titlebar .ghost') !== null;
        });
        expect(reachable).toBe(true);

        await window.locator('.tool-head button.shut').click();
        await expect(window).toHaveCount(0);
    });

    test('closes on Escape, and a second opening brings up the one there is', async ({ page }) => {
        await openAiSettings(page);
        const menu = await openSettings(page);
        await menu.getByRole('button', { name: /AI settings/ }).click();
        await expect(page.locator('dialog.tool[open][aria-label="AI settings"]')).toHaveCount(1);

        await page.locator('#aiToolRounds').focus();
        await page.keyboard.press('Escape');
        await expect(page.locator('dialog.tool[open][aria-label="AI settings"]')).toHaveCount(0);
    });
});

test.describe('the models', () => {
    test('the provider decides the fields', async ({ page }) => {
        const window = await openAiSettings(page);

        await window.locator('#aiProviderAnthropic').click();
        await expect(window.locator('#aiModelBaseUrl')).toHaveCount(0);
        await expect(window.locator('#aiModelMaxTokens')).toBeVisible();

        await window.locator('#aiProviderOther').click();
        await expect(window.locator('#aiModelBaseUrl')).toHaveAttribute('placeholder', 'http://localhost:11434');
        await expect(window.locator('#aiModelMaxTokens')).toHaveCount(0);

        await window.locator('#aiProviderGemini').click();
        await expect(window.locator('#aiModelBaseUrl')).toHaveAttribute('placeholder', /generativelanguage.*\(default\)/);
    });

    test('a new model is named in the head, saved with its key, and picked', async ({ page, request }) => {
        const window = await openAiSettings(page);

        await addModel(window, 'Bench Claude');

        const status = await (await request.get('/api/ai')).json();
        const made = status.connections.find((c) => c.name === 'Bench Claude');
        expect(made.kind).toBe('Anthropic');
        expect(made.configured).toBe(true);
        expect(status.selectedId).toBe(made.id);
        await expect(window.locator('#aiModelKey')).toHaveAttribute('placeholder', 'A key is stored. Type to replace it.');
        await expect(window.locator('#aiModelKey')).toHaveValue('');
    });

    test('Save is orange while there is something to save, and undoing a change takes it away', async ({ page }) => {
        const window = await openAiSettings(page);
        await addModel(window, 'Orange Test');
        const save = window.locator('#aiModelSave');
        await expect(save).not.toHaveClass(/savedPickerUnsaved/);

        const model = await window.locator('#aiModelId').inputValue();
        await window.locator('#aiModelId').fill('claude-something-else');
        await expect(save).toHaveClass(/savedPickerUnsaved/);

        await window.locator('#aiModelId').fill(model);
        await expect(save).not.toHaveClass(/savedPickerUnsaved/);
    });

    test('a model needs a key, and a name, and a server of ones own its address', async ({ page }) => {
        const window = await openAiSettings(page);
        await window.locator('#aiModelPickerChevron').click();
        await window.locator('#aiModelAdd').click();

        await window.locator('#aiModelSave').click();
        await expect(window.locator('#aiModelPickerError')).toHaveText('Name the model first.');

        await window.locator('#aiModelPickerName').fill('Nameless No More');
        await window.locator('#aiModelPickerName').press('Enter');
        await window.locator('#aiProviderAnthropic').click();
        await window.locator('#aiModelSave').click();
        await expect(window.locator('#aiModelError')).toHaveText('Anthropic needs an API key.');

        await window.locator('#aiProviderOther').click();
        await window.locator('#aiModelSave').click();
        await expect(window.locator('#aiModelError')).toHaveText('A server of your own needs its address.');
    });

    test('renames, duplicates and deletes from the menu', async ({ page, request }) => {
        const window = await openAiSettings(page);
        await addModel(window, 'Original');

        await window.locator('#aiModelPickerRename').click();
        await window.locator('#aiModelPickerName').fill('Renamed');
        await window.locator('#aiModelPickerName').press('Enter');
        await expect(window.locator('#aiModelPickerFace')).toContainText('Renamed');

        await window.locator('#aiModelPickerChevron').click();
        const row = window.locator('.savedPickerRow', { hasText: 'Renamed' });
        await row.hover();
        await row.locator('[data-act="duplicate"]').click();
        await expect(window.locator('#aiModelPickerFace')).toContainText('Renamed (copy)');

        //The copy brought its key.
        const status = await (await request.get('/api/ai')).json();
        expect(status.connections.find((c) => c.name === 'Renamed (copy)').configured).toBe(true);

        await window.locator('#aiModelPickerChevron').click();
        const copy = window.locator('.savedPickerRow', { hasText: 'Renamed (copy)' });
        await copy.hover();
        await copy.locator('[data-act="delete"]').click();
        await expect(window.locator('.savedPickerRow', { hasText: 'Renamed (copy)' })).toHaveCount(0);
    });

    test('Esc in the menu shuts the menu and leaves the window', async ({ page }) => {
        const window = await openAiSettings(page);

        await window.locator('#aiModelPickerChevron').click();
        await expect(window.locator('#aiModelPickerMenu')).toBeVisible();

        await page.keyboard.press('Escape');
        await expect(window.locator('#aiModelPickerMenu')).toHaveCount(0);
        await expect(window).toBeVisible();
    });

    test('a key saved for https is held back from an http address until typed again', async ({ page }) => {
        const window = await openAiSettings(page);
        await addModel(window, 'Https Test');
        await window.locator('#aiProviderOpenAI').click();
        await window.locator('#aiModelKey').fill('e2e-secret');
        await window.locator('#aiModelBaseUrl').fill('https://gateway.example');
        await window.locator('#aiModelId').fill('some-model');
        await window.locator('#aiModelSave').click();
        await expect(window.locator('#aiModelSave')).not.toHaveClass(/savedPickerUnsaved/);

        await window.locator('#aiModelBaseUrl').fill('http://gateway.example');
        await expect(window.locator('#aiKeyHeldBack')).toBeVisible();

        await window.locator('#aiModelKey').fill('typed-again');
        await expect(window.locator('#aiKeyHeldBack')).toHaveCount(0);

        await window.locator('#aiModelKey').fill('');
        await window.locator('#aiModelBaseUrl').fill('https://gateway.example');
        await expect(window.locator('#aiKeyHeldBack')).toHaveCount(0);
    });

    test('the price boxes show the published rates and say whose', async ({ page }) => {
        const window = await openAiSettings(page);

        await window.locator('#aiProviderAnthropic').click();
        await window.locator('#aiModelId').fill('claude-sonnet-5-5');
        await expect(window.locator('#aiPriceInput')).toHaveAttribute('placeholder', '2.00');
        await expect(window.locator('#aiPriceNote')).toContainText("claude-sonnet-5-5's published rates");

        await window.locator('#aiModelId').fill('nobody-knows-this-model');
        await expect(window.locator('#aiPriceInput')).toHaveAttribute('placeholder', '');
        await expect(window.locator('#aiPriceNote')).toContainText('No published rate');
    });

    test('Manage models in the assistant brings up this window', async ({ page }) => {
        const menu = await openSettings(page);
        await menu.getByRole('button', { name: /AI assistant/ }).click();
        const assistant = page.locator('dialog.tool[open][aria-label="AI assistant"]');
        await expect(assistant).toBeVisible();
        await settle(page);

        await assistant.locator('.aiModelToggle').click();
        await assistant.locator('.aiModelMenuManage').click();

        await expect(page.locator('dialog.tool[open][aria-label="AI settings"]')).toBeVisible();
    });
});

test.describe('speech and rounds', () => {
    test('speech to text is chosen here, kept, and heard by the microphone', async ({ page }) => {
        let window = await openAiSettings(page);

        await expect(window.locator('#aiSpeechBrowser')).toHaveAttribute('aria-pressed', 'true');
        await expect(window.locator('#aiSpeechNote')).toContainText('free');

        await window.locator('#aiSpeechProvider').click();
        await expect(window.locator('#aiSpeechNote')).toContainText('billed');

        const menu = await openSettings(page);
        await menu.getByRole('button', { name: /AI assistant/ }).click();
        const assistant = page.locator('dialog.tool[open][aria-label="AI assistant"]');
        await expect(assistant.locator('#aiMic')).toHaveAttribute('title', /An OpenAI or Gemini model writes it out/);

        await page.reload();
        await settle(page);
        window = await openAiSettings(page);
        await expect(window.locator('#aiSpeechProvider')).toHaveAttribute('aria-pressed', 'true');
    });

    test('the rounds are kept, and brought into range', async ({ page }) => {
        let window = await openAiSettings(page);
        const rounds = window.locator('#aiToolRounds');
        await expect(rounds).toHaveValue('30');

        await rounds.fill('5000');
        await rounds.press('Tab');
        await expect(rounds).toHaveValue('1000');

        await rounds.fill('12');
        await rounds.press('Tab');
        await expect(rounds).toHaveValue('12');

        await page.reload();
        await settle(page);
        window = await openAiSettings(page);
        await expect(window.locator('#aiToolRounds')).toHaveValue('12');
    });
});

test.describe('the MCP server', () => {
    let instrument;

    test.beforeAll(async () => {
        instrument = await startInstrument();
    });

    test.afterAll(async () => {
        await instrument.stop();
    });

    const freePort = () => new Promise((resolve) => {
        const finding = net.createServer();
        finding.listen(0, '127.0.0.1', () => {
            const { port } = finding.address();
            finding.close(() => resolve(port));
        });
    });

    test('Start shows a code, the chip says so when the window is shut, and Stop ends it', async ({ page }) => {
        const window = await openAiSettings(page);

        await window.locator('#liveLinkPort').fill(String(await freePort()));
        await window.locator('#liveLinkPort').press('Tab');
        await window.locator('#liveLinkStart').click();
        await expect(window.locator('#liveLinkCode')).toHaveText(/^\d{4} \d{4}$/);
        await expect(window.locator('#liveLinkStatus')).toContainText('Waiting for lec mcp on port');
        await expect(page.locator('#liveLinkChip')).toHaveCount(0);

        await window.locator('.tool-head button.shut').click();
        await expect(page.locator('#liveLinkChip')).toHaveText('MCP server waiting');

        await page.locator('#liveLinkChip').click();
        const again = page.locator('dialog.tool[open][aria-label="AI settings"]');
        await again.locator('#liveLinkStop').click();
        await expect(again.locator('#liveLinkStart')).toBeVisible();
    });

    ///
    ///The real thing: `lec mcp` started as a client starts it, spoken to on standard input and output,
    ///and the page's instruments answering through the link.
    ///
    test('a real lec mcp links the page and works its instruments', async ({ page, request }) => {
        test.setTimeout(120000);
        const root = path.join(__dirname, '..', '..', '..');
        await new Promise((resolve, reject) => {
            const build = spawn('dotnet', ['build', 'Cli/LabEquipmentController.Cli.csproj', '--nologo', '-v', 'q'], { cwd: root, stdio: 'ignore', shell: process.platform === 'win32' });
            build.on('exit', (code) => (code === 0 ? resolve() : reject(new Error(`build ${code}`))));
        });

        await connect(page, instrument.address);
        const port = await freePort();
        const server = spawn('dotnet', [path.join(root, 'Cli', 'bin', 'Debug', 'net10.0', 'lec.dll'), 'mcp', '--link-port', String(port)], { cwd: root, stdio: ['pipe', 'pipe', 'pipe'] });

        const answers = new Map();
        let waiting = '';
        server.stdout.on('data', (chunk) => {
            waiting += chunk.toString();
            let at;
            while ((at = waiting.indexOf('\n')) >= 0) {
                const line = waiting.slice(0, at);
                waiting = waiting.slice(at + 1);
                if (line.trim().length === 0) continue;
                const message = JSON.parse(line);
                if (message.id !== undefined) answers.set(message.id, message);
            }
        });

        let next = 1;
        const ask = async (method, params) => {
            const id = next++;
            server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
            await expect.poll(() => answers.has(id), { timeout: 30000 }).toBe(true);
            return answers.get(id).result;
        };

        try {
            await ask('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'Browser Test', version: '1' } });
            server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

            const window = await openAiSettings(page);
            await window.locator('#liveLinkPort').fill(String(port));
            await window.locator('#liveLinkPort').press('Tab');
            await window.locator('#liveLinkStart').click();
            const code = await window.locator('#liveLinkCode').textContent();

            const linked = await ask('tools/call', { name: 'link_tab', arguments: { code } });
            expect(linked.content[0].text).toContain('Linked to the bench page.');
            expect(linked.content[0].text).toContain('SDM3065X');
            await expect(window.locator('#liveLinkStatus')).toContainText('Linked to Browser Test');

            const asked = await ask('tools/call', { name: 'query', arguments: { command: '*IDN?' } });
            expect(asked.content[0].text).toContain('LEC-E2E-0001');
            await expect(window.locator('.liveLinkCall').first()).toContainText('query');

            await window.locator('.tool-head button.shut').click();
            await expect(page.locator('#liveLinkChip')).toHaveText('Linked to Claude');
        } finally {
            server.kill();
        }
    });
});
