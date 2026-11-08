//
//The AI assistant's window (UI-SPEC §5, The AI assistant), against the fake instrument and a model
//that is not there.
//
//The page asks its model through its own server's relay; here the relay is answered in the browser,
//from a script, so no request reaches the server's relay and nothing reaches a provider. Anything bound
//off this machine is refused outright. The connection is an OpenAI-compatible one, which takes no key,
//so none is set, typed or spent.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, openSettings, settle, scriptText } = require('./helpers');
const { startInstrument } = require('./instrument');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

///OpenAI's chat completion, saying words.
const said = (content) => ({
    choices: [{ index: 0, message: { role: 'assistant', content }, finish_reason: 'stop' }],
    usage: { prompt_tokens: 1200, completion_tokens: 40 }
});

///OpenAI's chat completion, calling tools: each [id, name, arguments].
const calls = (...list) => ({
    choices: [{
        index: 0,
        message: {
            role: 'assistant', content: null,
            tool_calls: list.map(([id, name, args]) => ({ id, type: 'function', function: { name, arguments: JSON.stringify(args) } }))
        },
        finish_reason: 'tool_calls'
    }],
    usage: { prompt_tokens: 1100, completion_tokens: 30 }
});

///
///A model answering from a script, one reply per request, the last one repeated, keeping what each
///request carried. Routed in the browser, so the server's relay never sees one.
///
async function scriptedModel(page, ...replies) {
    const sent = [];
    await page.unroute('**/api/ai/relay/**');
    await page.route('**/api/ai/relay/**', async (route) => {
        sent.push(route.request().postDataJSON());
        const reply = replies[Math.min(sent.length - 1, replies.length - 1)];
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(reply) });
    });
    return sent;
}

///
///The keyless connection this spec adds for each test, and the one picked before it, to put back.
///
///Put back after every test, because the server is the worker's and outlives this file: a
///connection left behind is one more in every other spec's pickers, and the datasheet window's
///spec counts them.
///
let added = null;
let before = null;

async function addKeyless(request) {
    before = (await (await request.get('/api/ai')).json()).selectedId;
    const reply = await (await request.post('/api/ai/connections?provider=OpenAiCompatible')).json();
    //Adding one selects it, which is how saying "use this one" reads here.
    added = reply.status.selectedId;
}

async function removeKeyless(request) {
    if (added) await request.delete(`/api/ai/connections/${added}`);
    if (before) await request.post(`/api/ai/connections/${before}/select`);
    added = before = null;
}

///The window, opened from the gear.
async function openAssistant(page) {
    const menu = await openSettings(page);
    await menu.getByRole('button', { name: /AI assistant/ }).click();
    const window = page.locator('dialog.tool[open][aria-label="AI Assistant"]');
    await expect(window).toBeVisible();
    await settle(page);
    return window;
}

///A question typed and sent with Enter, as a person sends one.
async function ask(window, question) {
    const box = window.locator('textarea.prompt');
    await box.fill(question);
    await box.press('Enter');
}

test.beforeEach(async ({ page, request }) => {
    //Nothing leaves this machine.
    await page.route((url) => !/^(localhost|127\.0\.0\.1)$/.test(url.hostname), (route) => route.abort());
    await addKeyless(request);
    await freshBench(page, request);
    await connect(page, instrument.address);
});

test.afterEach(async ({ request }) => {
    await removeKeyless(request);
});

test.describe('the window', () => {
    ///
    ///Under the gear, beside the connection it spends, as the desktop has it under Tools beside AI
    ///Connection: the chats, the conversation and the box, and the row under the box.
    ///
    test('opens from the gear with the chats, the conversation and the row', async ({ page }) => {
        const window = await openAssistant(page);

        await expect(window.locator('.chats-panel')).toContainText('Chats');
        await expect(window.getByRole('button', { name: 'New Chat' })).toBeVisible();
        await expect(window.locator('.kept')).toHaveText("Kept by the bench's server.");
        await expect(window.locator('.context')).toContainText('SDM3065X');

        //The box opens on a question that works as it stands.
        await expect(window.locator('textarea.prompt')).toHaveValue('What is open on the bench, and what does each instrument read right now?');

        for (const label of ['Using:', 'Effort:', 'Changes:'])
            await expect(window.locator('.assistant-row')).toContainText(label);
        for (const name of ['Attach Files…', 'Screenshot', 'Tools…', 'Send'])
            await expect(window.locator('.assistant-row').getByRole('button', { name, exact: true })).toBeVisible();

        //Every visit starts on the middle rung.
        await expect(window.locator('.assistant-row select').nth(2)).toHaveValue('AutoRead');
    });

    ///
    ///The question goes, every call is a card with what it was told, the answer lands, and the chat is
    ///kept from its first question on: its row appears in the list.
    ///
    test('answers through the relay, with every call on a card, and keeps the chat', async ({ page }) => {
        const sent = await scriptedModel(page,
            calls(['c1', 'list_instruments', {}]),
            calls(['c2', 'query', { instrument: 'dmm', command: '*IDN?' }]),
            said('The meter is the SDM3065X at the address above.'));
        const window = await openAssistant(page);

        await ask(window, 'Which meter is this?');

        await expect(window.locator('.turn.answer')).toContainText('The meter is the SDM3065X');
        const cards = window.locator('.turn.tool');
        await expect(cards).toHaveCount(2);
        await expect(cards.nth(0)).toContainText('list_instruments');
        await expect(cards.nth(0)).toContainText('ran without asking');
        await expect(cards.nth(1)).toContainText('dmm · *IDN?');
        await expect(cards.nth(1)).toContainText('LEC-E2E-0001');

        //Three requests, each with the tools and the instructions the page wrote.
        expect(sent.length).toBe(3);
        expect(sent[0].tools.map((t) => t.function.name)).toContain('send');
        expect(JSON.stringify(sent[0].messages[0])).toContain("the bench's web page");

        //What reached the instrument is the one query.
        expect(instrument.asked(/^\*IDN\?/).length).toBeGreaterThanOrEqual(2);

        await expect(window.locator('.chat-row.on .title')).toHaveText('Which meter is this?');
    });

    ///
    ///On the middle rung a change waits on its card, and a decline sends nothing.
    ///
    test('a send waits for Approve, and a decline sends nothing', async ({ page }) => {
        await scriptedModel(page,
            calls(['s1', 'send', { instrument: 'dmm', command: 'CONF:VOLT:DC' }]),
            said('Left as it was.'));
        const window = await openAssistant(page);

        await ask(window, 'Set the meter to DC volts.');

        const card = window.locator('.turn.tool.waiting');
        await expect(card).toContainText('waits for you: this changes the instrument');
        await expect(card.getByRole('button', { name: 'Approve and Send' })).toBeVisible();
        await card.getByRole('button', { name: 'Decline' }).click();

        await expect(window.locator('.turn.tool')).toContainText('declined');
        await expect(window.locator('.turn.answer')).toContainText('Left as it was.');
        expect(instrument.asked(/CONF:VOLT:DC/)).toHaveLength(0);
    });
});

test.describe('the commands', () => {
    ///
    ///A slash lists the window's commands, narrowed as the name is typed; Esc puts the list away and
    ///leaves the window open; a command written out runs on Enter.
    ///
    test('a slash lists them, Esc puts them away, and one written out runs', async ({ page }) => {
        const window = await openAssistant(page);
        const box = window.locator('textarea.prompt');

        await box.fill('/');
        const list = window.locator('.slash li');
        await expect(list).toHaveCount(9);
        await expect(list.first()).toContainText('/new');

        await box.fill('/co');
        await expect(list).toHaveCount(1);
        await expect(list.first()).toContainText('/compact');

        await box.press('Escape');
        await expect(list).toHaveCount(0);
        await expect(window).toBeVisible();

        await box.fill('/new');
        await box.press('Tab');
        await expect(box).toHaveValue('/new ');
        await box.press('Enter');
        await expect(window.locator('.assistant-status')).toContainText('New conversation.');
        await expect(box).toHaveValue('');
    });
});

test.describe('kept chats', () => {
    ///
    ///New Chat starts another; the kept one opens again from its row, with the line saying so, and
    ///carries on.
    ///
    test('a kept chat opens again with a line saying so', async ({ page }) => {
        await scriptedModel(page, said('It is a multimeter.'));
        const window = await openAssistant(page);
        const question = `What kind of instrument is it? (${Date.now()})`;

        await ask(window, question);
        await expect(window.locator('.turn.answer')).toContainText('It is a multimeter.');

        await window.getByRole('button', { name: 'New Chat' }).click();
        await expect(window.locator('.turn')).toHaveCount(0);

        await window.locator('.chat-row', { hasText: question }).click();
        await expect(window.locator('.turn.you')).toContainText(question);
        await expect(window.locator('.turn.note')).toContainText('This chat was opened again on');
    });

    ///Closing the window is not clearing it: the conversation is there when it opens again.
    test('closing the window keeps the conversation', async ({ page }) => {
        await scriptedModel(page, said('Still here.'));
        let window = await openAssistant(page);

        await ask(window, 'Remember this.');
        await expect(window.locator('.turn.answer')).toContainText('Still here.');

        await window.locator('.tool-head button.shut').click();
        await expect(window).toHaveCount(0);

        window = await openAssistant(page);
        await expect(window.locator('.turn.you')).toContainText('Remember this.');
        await expect(window.locator('.turn.answer')).toContainText('Still here.');
    });
});

test.describe('scripts and files', () => {
    ///
    ///A proposed sequence goes into the multi-instrument editor by Use This Script, over the example it
    ///opens on once that is agreed to, and is not run.
    ///
    test('Use This Script puts a proposed sequence in the multi-instrument editor', async ({ page }) => {
        await scriptedModel(page,
            calls(['p1', 'propose_script', { kind: 'sequence', script: 'PRINT "from the assistant"' }]),
            said('The draft is on the card.'));
        const window = await openAssistant(page);

        await ask(window, 'Write a sequence that prints a line.');
        const card = window.locator('.turn.tool', { hasText: 'propose_script' });
        await expect(card.locator('pre.script')).toHaveText('PRINT "from the assistant"');

        page.once('dialog', (dialog) => dialog.accept());
        await card.getByRole('button', { name: 'Use This Script' }).click();

        await expect(page.locator('dialog.tool[open][aria-label="Multi-Instrument Scripts"]')).toBeVisible();
        await expect.poll(() => scriptText(page)).toBe('PRINT "from the assistant"');
    });

    ///
    ///A file chosen goes to the box as a chip; sent, it is listed in the files panel, ticked, and the
    ///arrow in the margin counts it.
    ///
    test('a file goes with the question and is listed in the files panel', async ({ page }) => {
        const sent = await scriptedModel(page, said('Read it.'));
        const window = await openAssistant(page);

        await window.locator('input[type=file]').setInputFiles({
            name: 'settings.csv', mimeType: 'text/csv', buffer: Buffer.from('channel,volts\n1,5.0\n')
        });
        await expect(window.locator('.chip')).toContainText('settings.csv');

        await ask(window, 'What does the file say?');
        await expect(window.locator('.turn.answer')).toContainText('Read it.');
        expect(JSON.stringify(sent[0].messages)).toContain('channel,volts');

        await expect(window.locator('.margin-arrow.right .count')).toHaveText('1');
        await window.locator('.margin-arrow.right').click();
        const row = window.locator('.files-panel .file-row', { hasText: 'settings.csv' });
        await expect(row.locator('input[type=checkbox]')).toBeChecked();
    });
});
