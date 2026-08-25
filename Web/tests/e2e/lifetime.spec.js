//
//How long the bench outlives the window that opened it.
//
//Closing the window closes the bench. The desktop takes every connection with it when MainForm goes,
//and a build that hands the next visitor the last one's sessions is offering a console onto an
//instrument that may since have been switched off, moved or unplugged - and one that now answers to a
//different address is worse than one that does not answer at all.
//
//But a reload is not a closing, and surviving one is the main thing this build has over the desktop: a
//sweep that outlives a refresh. The two are told apart by sessionStorage, which survives a reload of a
//tab and nothing else - see index.html and BenchService.PageOpenedAsync.
//
const { test, expect } = require('./fixtures');
const { gotoBench, clearBench, connect, BOOT_MS } = require('./helpers');
const { startInstrument } = require('./instrument');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

//One bench between them, so they take their turns rather than clearing each other's.
test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ request }) => {
    await clearBench(request);
});

///
///A reload is the same window coming back, and the bench is still there when it does.
///
test('a reload keeps the bench', async ({ page }) => {
    await gotoBench(page);
    await connect(page, instrument.address);

    await page.reload();
    await expect(page.locator('.tabstrip .tab')).toHaveCount(1, { timeout: BOOT_MS });
});

///
///Opening the app is not, and it starts on an empty bench.
///
///Answered before the runtime is started, so the page draws an empty bench rather than drawing the last
///window's consoles and taking them away again - which would be a flicker rather than a fix. Asserted
///without polling for the same reason: by the time the page is up, it is already settled.
///
test('opening the app afresh starts on an empty bench', async ({ browser, server, request }) => {
    const first = await browser.newContext({ baseURL: server });
    const a = await first.newPage();
    await gotoBench(a);
    await connect(a, instrument.address);
    expect(await (await request.get('/api/sessions')).json()).toHaveLength(1);

    //The window closed. Nothing is watching the bench now.
    await first.close();
    await new Promise((done) => setTimeout(done, 700));

    const second = await browser.newContext({ baseURL: server });
    const b = await second.newPage();

    try {
        await gotoBench(b);
        await expect(b.locator('.group.consoles .none')).toBeVisible();
        await expect(b.locator('.tabstrip .tab')).toHaveCount(0);
        expect(await (await request.get('/api/sessions')).json()).toHaveLength(0);
    } finally {
        await second.close();
    }
});

///
///A detached console is not an opening, and does not clear anything.
///
///It is a window the app itself opened onto a session that already exists. It never asks the question,
///which is why it is safe to ask it without caring who else is watching.
///
test('a detached console leaves the bench alone', async ({ page, context }) => {
    await gotoBench(page);
    await connect(page, instrument.address);

    const href = await page.locator('.tabstrip .tab a.pop').getAttribute('href');
    const other = await context.newPage();

    try {
        await other.goto('/' + href);
        await expect(other.locator('button.quick').first()).toBeEnabled({ timeout: BOOT_MS });
        await expect(page.locator('.tabstrip .tab')).toHaveCount(1);
    } finally {
        await other.close();
    }
});

///
///Opening the bench page again *is* an opening, whoever else has it open.
///
///This is the half that was missing, and the one the fault was hiding behind: guarded on nobody
///watching, a tab left open - or one the browser restored, or one whose socket had not yet been
///reaped - meant the opening did nothing and the app came up on the last bench again.
///
test('a second window opening the app clears it, watchers or not', async ({ page, context }) => {
    await gotoBench(page);
    await connect(page, instrument.address);

    const other = await context.newPage();

    try {
        await gotoBench(other);
        await expect(other.locator('.group.consoles .none')).toBeVisible();
        await expect(other.locator('.tabstrip .tab')).toHaveCount(0);
        await expect(page.locator('.tabstrip .tab')).toHaveCount(0, { timeout: 20000 });
    } finally {
        await other.close();
    }
});
