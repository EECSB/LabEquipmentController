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
    const window = page.locator('dialog.tool[open][aria-label="AI assistant"]');
    await expect(window).toBeVisible();
    await settle(page);
    return window;
}

///A question typed and sent with Enter, as a person sends one.
async function ask(window, question) {
    const box = window.locator('#aiPrompt');
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
    ///Under the gear, beside the connection it spends: GDS Viewer's window, with the chats and the
    ///files open beside the conversation and everything about the next question in the box's frame.
    ///
    test('opens from the gear with the chats, the files and the box', async ({ page }) => {
        const window = await openAssistant(page);

        await expect(window.locator('#aiChats')).toContainText('Chats');
        await expect(window.locator('#aiChatsNew')).toBeVisible();
        await expect(window.locator('.aiChatsKept')).toHaveText("Kept by the bench's server");
        await expect(window.locator('#aiFiles')).toContainText('In this conversation');
        await expect(window.locator('#aiContext')).toContainText('SDM3065X');
        await expect(window.locator('.tool-head')).toContainText('SDM3065X');

        //The box starts empty, its placeholder saying what to do.
        await expect(window.locator('#aiPrompt')).toHaveValue('');

        const frame = window.locator('.aiBox');
        for (const name of ['Attach files', 'Take a picture with the camera', 'Attach a screenshot', 'Tools and compacting', 'AI model', 'Send', 'Speak your question'])
            await expect(frame.getByRole('button', { name, exact: true })).toBeVisible();

        //Every visit starts on the middle rung.
        await expect(window.locator('#aiMode')).toHaveValue('AutoRead');

        //What the next question costs, under the box.
        await expect(window.locator('#aiMeter')).toContainText('Next request: about');
    });

    ///
    ///The window's own proportions, which is the whole point of having copied them: the words and
    ///the row are one frame, the row is one line, and the panels' arrows sit in the margins, centred.
    ///
    test('keeps the box and its row in one frame, and the arrows in the margins', async ({ page }) => {
        const window = await openAssistant(page);

        const frame = await window.locator('.aiBox').boundingBox();
        const words = await window.locator('#aiPrompt').boundingBox();
        const bar = await window.locator('.aiBoxBar').boundingBox();
        const talk = await window.locator('#aiTranscript').boundingBox();

        expect(words.y).toBeGreaterThan(frame.y);
        expect(bar.y).toBeGreaterThanOrEqual(words.y + words.height - 1);
        expect(bar.y + bar.height).toBeLessThanOrEqual(frame.y + frame.height);
        expect(bar.height).toBeLessThan(40);
        expect(talk.y + talk.height).toBeLessThanOrEqual(frame.y);

        await window.locator('#aiChatsClose').click();
        await window.locator('#aiFilesClose').click();

        const body = await window.locator('.aiBody').boundingBox();
        const row = await window.locator('.aiChatRow').boundingBox();
        const left = await window.locator('#aiChatsToggle').boundingBox();
        const right = await window.locator('#aiFilesToggle').boundingBox();

        //In the margins rather than over the conversation, the same distance from each edge.
        expect(left.x + left.width).toBeLessThanOrEqual(row.x);
        expect(right.x).toBeGreaterThanOrEqual(row.x + row.width);
        expect(left.x - body.x).toBeCloseTo(body.x + body.width - (right.x + right.width), 0);

        //Centred top to bottom, and big enough to see and to aim at.
        expect(left.y + left.height / 2).toBeCloseTo(row.y + row.height / 2, 0);
        expect(left.width).toBeGreaterThanOrEqual(20);
        expect(left.height).toBeGreaterThanOrEqual(44);
    });

    ///An example goes in the box with the keyboard there, and nothing is sent.
    test('an example goes in the box to send or change first', async ({ page }) => {
        const sent = await scriptedModel(page, said('Not asked.'));
        const window = await openAssistant(page);

        const example = window.locator('.aiExample').first();
        const words = await example.textContent();
        await example.click();

        await expect(window.locator('#aiPrompt')).toHaveValue(words);
        await expect(window.locator('#aiPrompt')).toBeFocused();
        expect(sent).toHaveLength(0);
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

        await expect(window.locator('.aiBubbleUser')).toHaveText('Which meter is this?');
        await expect(window.locator('.aiBubbleAssistant')).toContainText('The meter is the SDM3065X');
        const cards = window.locator('.aiCard');
        await expect(cards).toHaveCount(2);
        await expect(cards.nth(0)).toContainText('Looked: list instruments');
        await expect(cards.nth(0).locator('.aiBadge')).toHaveText('auto');
        await expect(cards.nth(1)).toContainText('dmm · *IDN?');
        await expect(cards.nth(1)).toContainText('LEC-E2E-0001');

        //Three requests, each with the tools and the instructions the page wrote.
        expect(sent.length).toBe(3);
        expect(sent[0].tools.map((t) => t.function.name)).toContain('send');
        expect(JSON.stringify(sent[0].messages[0])).toContain("the bench's web page");

        //What reached the instrument is the one query.
        expect(instrument.asked(/^\*IDN\?/).length).toBeGreaterThanOrEqual(2);

        await expect(window.locator('.aiChatItemOn .aiChatTitle')).toHaveText('Which meter is this?');

        //And what it was billed is under the answer, and in the line under the box.
        await expect(window.locator('.aiUsage')).toContainText('3 requests');
        await expect(window.locator('#aiSpent')).toContainText('Spent:');
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

        const card = window.locator('.aiCardChange');
        await expect(card).toContainText('Wants to change an instrument: send');
        await expect(card.getByRole('button', { name: 'Approve and Send' })).toBeVisible();
        await card.getByRole('button', { name: 'Decline' }).click();

        await expect(card).toContainText('Declined');
        await expect(window.locator('.aiBubbleAssistant')).toContainText('Left as it was.');
        expect(instrument.asked(/CONF:VOLT:DC/)).toHaveLength(0);
    });
});

test.describe('the box', () => {
    ///
    ///The model and its effort are one button, and its menu goes away as a menu should: a level
    ///chosen closes it, Escape steps back out of the levels first, a press elsewhere closes it.
    ///
    test('the model button opens the models and the effort, and goes away', async ({ page }) => {
        const window = await openAssistant(page);
        const toggle = window.locator('.aiModelToggle');
        const menu = window.locator('.aiModelMenu');

        await toggle.click();
        await expect(menu.locator('[role=menuitemradio][aria-checked=true]')).toHaveCount(1);
        await expect(menu.locator('.aiModelMenuManage')).toBeVisible();

        await page.keyboard.press('Escape');
        await expect(menu).toHaveCount(0);
        //Esc in the menu is the menu's: the window is still up.
        await expect(window).toBeVisible();

        await toggle.click();
        await expect(menu).toBeVisible();
        await window.locator('#aiTranscript').click();
        await expect(menu).toHaveCount(0);

        await toggle.click();
        await menu.locator('.aiModelMenuManage').click();
        await expect(menu).toHaveCount(0);
        await expect(page.locator('dialog[open]', { hasText: 'API key' }).first()).toBeVisible();
    });

    ///The picker counts what is off on its switch, and a press elsewhere puts it away.
    test('the tool picker switches tools off and counts them', async ({ page }) => {
        const window = await openAssistant(page);
        const toggle = window.locator('#aiToolPickerToggle');

        await toggle.click();
        const picker = window.locator('#aiToolPicker');
        await expect(picker).toContainText('Changing an instrument');

        await picker.locator('input[data-tool="send"]').uncheck();
        await expect(window.locator('#aiToolPickerCount')).toHaveText('1');

        await window.locator('#aiTranscript').click();
        await expect(picker).toHaveCount(0);

        await toggle.click();
        await picker.locator('input[data-tool="send"]').check();
        await expect(window.locator('#aiToolPickerCount')).toHaveCount(0);
    });

    ///The (i) takes the costs off the line under the box; the card says them while it is pointed at.
    test('the (i) moves the costs between the line and its card', async ({ page }) => {
        const window = await openAssistant(page);

        await expect(window.locator('#aiMeter')).toBeVisible();
        await window.locator('#aiMeterToggle').click();
        await expect(window.locator('#aiMeter')).toHaveCount(0);

        await window.locator('#aiMeterToggle').hover();
        await expect(window.locator('#aiMeterCard')).toBeVisible();
        await expect(window.locator('#aiMeterCard')).toContainText('Next request: about');
    });
});

test.describe('the commands', () => {
    ///
    ///A slash lists the window's commands, narrowed as the name is typed; Esc puts the list away and
    ///leaves the window open; a command written out runs on Enter.
    ///
    test('a slash lists them, Esc puts them away, and one written out runs', async ({ page }) => {
        const window = await openAssistant(page);
        const box = window.locator('#aiPrompt');

        await box.fill('/');
        const list = window.locator('.aiSlashItem');
        await expect(list).toHaveCount(10);
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
        await expect(box).toHaveValue('');
    });
});

test.describe('kept chats', () => {
    ///
    ///New chat starts another; the kept one opens again from its row, with the line saying so, and
    ///carries on.
    ///
    test('a kept chat opens again with a line saying so', async ({ page }) => {
        await scriptedModel(page, said('It is a multimeter.'));
        const window = await openAssistant(page);
        const question = `What kind of instrument is it? (${Date.now()})`;

        await ask(window, question);
        await expect(window.locator('.aiBubbleAssistant')).toContainText('It is a multimeter.');

        await window.locator('#aiChatsNew').click();
        await expect(window.locator('.aiBubbleUser')).toHaveCount(0);
        await expect(window.locator('#aiChatNow')).toContainText('Not kept yet');

        await window.locator('.aiChatItem', { hasText: question }).click();
        await expect(window.locator('.aiBubbleUser')).toContainText(question);
        await expect(window.locator('.aiTurnNote')).toContainText('This chat was opened again on');
    });

    ///Closing the window is not clearing it: the conversation is there when it opens again.
    test('closing the window keeps the conversation', async ({ page }) => {
        await scriptedModel(page, said('Still here.'));
        let window = await openAssistant(page);

        await ask(window, 'Remember this.');
        await expect(window.locator('.aiBubbleAssistant')).toContainText('Still here.');

        await window.locator('.tool-head button.shut').click();
        await expect(window).toHaveCount(0);

        window = await openAssistant(page);
        await expect(window.locator('.aiBubbleUser')).toContainText('Remember this.');
        await expect(window.locator('.aiBubbleAssistant')).toContainText('Still here.');
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
        const card = window.locator('.aiCard[data-tool="propose_script"]');
        await expect(card.locator('.aiCodeScript')).toHaveText('PRINT "from the assistant"');

        page.once('dialog', (dialog) => dialog.accept());
        await card.getByRole('button', { name: 'Use This Script' }).click();

        await expect(page.locator('dialog.tool[open][aria-label="Multi-Instrument Scripts"]')).toBeVisible();
        await expect.poll(() => scriptText(page)).toBe('PRINT "from the assistant"');
    });

    ///
    ///A file chosen goes to the box as a chip; sent, it is listed in the files panel, ticked, and the
    ///arrow in the margin counts it once the panel is closed.
    ///
    test('a file goes with the question and is listed in the files panel', async ({ page }) => {
        const sent = await scriptedModel(page, said('Read it.'));
        const window = await openAssistant(page);

        await window.locator('input[type=file]').setInputFiles({
            name: 'settings.csv', mimeType: 'text/csv', buffer: Buffer.from('channel,volts\n1,5.0\n')
        });
        await expect(window.locator('#aiPending .aiChip')).toContainText('settings.csv');

        await ask(window, 'What does the file say?');
        await expect(window.locator('.aiBubbleAssistant')).toContainText('Read it.');
        expect(JSON.stringify(sent[0].messages)).toContain('channel,volts');

        const row = window.locator('#aiFiles .aiFileRow', { hasText: 'settings.csv' });
        await expect(row.locator('input[type=checkbox]')).toBeChecked();

        await window.locator('#aiFilesClose').click();
        await expect(window.locator('#aiFilesCount')).toHaveText('1');
        await window.locator('#aiFilesToggle').click();
        await expect(row.locator('input[type=checkbox]')).toBeChecked();
    });
});
