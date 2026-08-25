//
//One console, against the fake instrument: what it says it is talking to, what it offers, and what
//happens when a command is sent.
//
//This is the first spec that needs an instrument, and it gets one without a bench - see instrument.js.
//The fake answers as an SDM3065X, so the console under test is a multimeter: twelve quick commands,
//eight readout functions, and no capture of either kind. That single identity exercises both halves of
//UI-SPEC §7 at once.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, quick, consoleButton, boxOf, rowGaps, settle } = require('./helpers');
const { startInstrument, HEADERS } = require('./instrument');

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, instrument.address);
});

test.describe('connecting', () => {
    ///
    ///A tab per instrument, naming what it recognised and where it is.
    ///
    test('opens a tab carrying the profile name and the address', async ({ page }) => {
        const tab = page.locator('.tabstrip .tab');
        await expect(tab).toContainText(instrument.profile);
        await expect(tab).toContainText(instrument.address);
    });

    ///
    ///Opening the console in a browser tab of its own lives on the tab, beside the ✕.
    ///
    ///It is a thing you do *to* a tab, which is where a browser puts its own ✕ for the same reason -
    ///and up there it can be reached without first bringing that console to the front. It used to be a
    ///labelled button on the identity line, costing a row's width to say what a glyph says.
    ///
    ///A link rather than a button calling window.open: nothing can pop-up-block an address, and the
    ///browser's own conventions come free. The target names the tab after the session, so pressing it
    ///twice returns to the console already open instead of starting a second.
    ///
    test('the tab carries the way to open this console in a browser tab', async ({ page }) => {
        const pop = page.locator('.tabstrip .tab a.pop');

        await expect(pop).toHaveCount(1);
        await expect(pop).toHaveAttribute('href', /console\?session=.+&detached=1/);
        await expect(pop).toHaveAttribute('target', /^lec-console-/);

        //And the console below it does not repeat the offer.
        await expect(pane(page).getByRole('link', { name: /Open in new tab/ })).toHaveCount(0);
    });

    ///
    ///The identity line says all four things on one line: address, transport, what it was recognised
    ///as, and the reply itself.
    ///
    ///One line and not two. The caption used to say the profile and the address, and the line under it
    ///said the same two again - so the console opened by telling you where you were twice.
    ///
    test('the identity line names the address, the transport, the profile and the reply', async ({ page }) => {
        const line = pane(page).locator('.idline');

        await expect(line).toContainText(instrument.address);
        await expect(line).toContainText('Raw socket');
        await expect(line).toContainText(instrument.profile);
        await expect(line).toContainText(instrument.identity);
    });

    ///
    ///And the instrument was asked exactly one thing to get there.
    ///
    ///Connecting is `*IDN?` and nothing else. A connect that sent setup commands of its own would be
    ///changing an instrument's state to look at it.
    ///
    test('connecting asks the instrument only who it is', async ({ page }) => {
        expect(instrument.received.filter((c) => /^\*IDN\?/i.test(c)).length).toBeGreaterThan(0);
        expect(instrument.received.every((c) => /^\*IDN\?/i.test(c))).toBe(true);
    });
});

test.describe('what the console offers', () => {
    ///
    ///The quick commands come from the family's catalog, not from a fixed list.
    ///
    test('the multimeter quick commands are on the strip', async ({ page }) => {
        for (const label of ['DC V', 'AC V', '2-wire Ω', 'Freq', '*IDN?']) {
            await expect(quick(page, label)).toHaveCount(1);
        }
    });

    ///
    ///A glyph drawn in SVG is the nominal size: eighteen against fourteen, which is the desktop's
    ///sixteen against twelve.
    ///
    ///The drawn ones need no optical correction - they are outlines on the same 64-unit grid as
    ///AppIcons.Drawn, inset from their own canvas already. Discover Commands wears the magnifier.
    ///
    test('a drawn glyph is 18px', async ({ page }) => {
        const box = await boxOf(page, '.pane:not([hidden]) .toolbar button svg');
        expect(box.width).toBe(18);
        expect(box.height).toBe(18);
    });

    ///
    ///Present and dead, never missing.
    ///
    ///A button that is not there tells you nothing; a button that is there and dead tells you the tool
    ///exists and this instrument is not one it works on. The reason goes in the tooltip - UI-SPEC §7.
    ///
    test('capture is greyed for an instrument that cannot do it, and says why', async ({ page }) => {
        const screen = consoleButton(page, 'Capture Screen');
        const wave = consoleButton(page, 'Capture Waveform');

        await expect(screen).toBeDisabled();
        await expect(wave).toBeDisabled();

        await expect(screen).toHaveAttribute('title', /No screen-capture command is documented/);
        await expect(wave).toHaveAttribute('title', /No waveform-transfer dialect is documented/);
    });

    ///
    ///And the tool this instrument *can* do is live, which is what makes the greying mean something.
    ///
    test('live readout is offered on an instrument that has readouts', async ({ page }) => {
        await expect(consoleButton(page, /Live Readout/)).toBeEnabled();
    });

    ///
    ///A console opens by saying what it is attached to. The desktop prints these three the moment
    ///a session is made (MainForm), and they stay at the top of the log for as long as it lives -
    ///so the same words, in the same order, and the identity is the instrument's own answer.
    ///
    test('opens on the connection banner, as the desktop console does', async ({ page }) => {
        const lines = pane(page).locator('.console > div');

        await expect(lines.nth(0)).toHaveText(/^--- connected to \S+ via .+ ---$/);
        await expect(lines.nth(1)).toHaveText('*IDN? -> ' + instrument.identity);
        await expect(lines.nth(2)).toHaveText(/^--- quick commands loaded for: .+ ---$/);
    });

    ///
    ///Connect on an address that is already open brings its console to the front and says so there.
    ///
    ///One session per instrument is not tidiness: a Rigol DS2202 wedges its firmware if a second TCP
    ///session is opened against it, so neither build dials an address it already holds. Which leaves
    ///a press that appears to do nothing whenever that console was already the one on top - and the
    ///line in the log is the whole of the answer, on the desktop and here.
    ///
    test('says so when Connect is pressed on an instrument already open', async ({ page }) => {
        await page.locator('#addr').fill(instrument.address);
        await page.getByRole('button', { name: 'Connect', exact: true }).click();

        await expect(pane(page).locator('.console'))
            .toContainText(`--- already connected to ${instrument.address}; this is its console ---`);

        //And no second tab, because there is no second session.
        await expect(page.locator('.tabstrip .tab')).toHaveCount(1);
    });
    ///
    ///And the log buttons are live, because there is a log. Clearing it takes them down with it and
    ///puts the hint back - which is where that hint belongs: an empty console, not a new one.
    ///
    test('Clear Log takes the banner, the hint, and both log buttons', async ({ page }) => {
        await expect(consoleButton(page, 'Clear Log')).toBeEnabled();
        await expect(consoleButton(page, 'Save Log')).toBeEnabled();

        await consoleButton(page, 'Clear Log').click();

        await expect(consoleButton(page, 'Clear Log')).toBeDisabled();
        await expect(consoleButton(page, 'Save Log')).toBeDisabled();
        await expect(pane(page).locator('.console .empty'))
            .toHaveText('Type a SCPI command below, or press a quick command above.');
    });

    ///
    ///Send needs something to send.
    ///
    test('Send is dead while the command box is empty', async ({ page }) => {
        await expect(consoleButton(page, 'Send')).toBeDisabled();

        await pane(page).locator('.row.cmd input').fill('*IDN?');
        await expect(consoleButton(page, 'Send')).toBeEnabled();
    });
});

test.describe('sending a command', () => {
    ///
    ///Typed, Enter, and the exchange lands in the log: the command echoed, the reply under it.
    ///
    test('Enter sends what is typed and the reply arrives in the log', async ({ page }) => {
        await pane(page).locator('.row.cmd input').fill('*IDN?');
        await pane(page).locator('.row.cmd input').press('Enter');

        const log = pane(page).locator('.console');
        await expect(log).toContainText('> *IDN?');
        await expect(log).toContainText(instrument.identity);

        //And the instrument really was asked - the log is the app's account of the exchange, this is
        //the exchange.
        await expect.poll(() => instrument.asked(/^\*IDN\?/i).length).toBeGreaterThan(1);
    });

    ///
    ///A quick command sends the SCPI its tooltip shows, not its label.
    ///
    test('a quick command sends the command behind it', async ({ page }) => {
        await expect(quick(page, 'DC V')).toHaveAttribute('title', 'MEASure:VOLTage:DC?');
        await quick(page, 'DC V').click();

        await expect(pane(page).locator('.console')).toContainText('> MEASure:VOLTage:DC?');
        await expect.poll(() => instrument.asked(/MEASure:VOLTage:DC\?/).length).toBe(1);
    });

    ///
    ///Once there is a log there is something to do with it.
    ///
    test('the log buttons come alive once there is a log', async ({ page }) => {
        await quick(page, 'DC V').click();
        await expect(pane(page).locator('.console')).toContainText('MEASure:VOLTage:DC?');

        await expect(consoleButton(page, 'Clear Log')).toBeEnabled();
        await expect(consoleButton(page, 'Save Log')).toBeEnabled();

        await consoleButton(page, 'Clear Log').click();
        await expect(consoleButton(page, 'Clear Log')).toBeDisabled();
    });
});

test.describe('the command row', () => {
    ///
    ///Send against the box it acts on, then the wide step, then the two that act on the log above.
    ///
    ///That step is the whole point of the row: Send answers the box to its left and Clear Log and Save
    ///Log answer the log, and the distance is what says they are different jobs sharing a row.
    ///InstrumentConsole draws it as 0/40/6; the web reads 6/52/6 at this font - UI-SPEC §1.
    ///
    test('is spaced six, fifty-two, six', async ({ page }) => {
        const gaps = await rowGaps(page, '.pane:not([hidden]) .row.cmd');
        expect(gaps.map((g) => g.px)).toEqual([6, 52, 6]);
    });

    ///
    ///And the log above it fills the panel rather than stopping short of it.
    ///
    ///The two panels are stretched to the same height by the grid, so a fixed-height log left a field of
    ///empty card under it whenever the panel beside it was the taller.
    ///
    test('the log takes the height the panel has to give', async ({ page }) => {
        const panel = await boxOf(page, '.pane:not([hidden]) .split > .group');
        const log = await boxOf(page, '.pane:not([hidden]) .console');
        const row = await boxOf(page, '.pane:not([hidden]) .row.cmd');

        //Everything between the log's foot and the card's is the row and the air either side of it.
        expect(panel.bottom - log.bottom).toBeLessThan(row.height + 30);
        expect(log.height).toBeGreaterThanOrEqual(360);
    });
});

test.describe('disconnecting', () => {
    ///
    ///The ✕ takes the tab and the session with it.
    ///
    test('closing the tab disconnects the instrument', async ({ page, request }) => {
        await page.locator('.tabstrip .tab .shut').click();

        await expect(page.locator('.tabstrip')).toHaveCount(0);
        await expect(page.locator('.group.consoles .none')).toBeVisible();

        const open = await (await request.get('/api/sessions')).json();
        expect(open).toHaveLength(0);
    });
});

//
//Discover Commands, which is a question before it is a window.
//
//There is no universal way to enumerate an instrument's command set - the programming manual is
//ground truth - but SCPI-99 does define one runtime query, SYSTem:HELP:HEADers?, and an
//instrument that answers it is telling you about the firmware in front of you rather than about
//the guide that shipped with the model. So the desktop asks, prints what came back, and falls
//back to the bundled catalog only when the instrument cannot say. Most budget instruments cannot,
//which is why the fallback is the ordinary path and why the web went straight to it and never
//asked at all.
//
test.describe('Discover Commands', () => {
    ///The console's log, line by line.
    function logLines(page) {
        return pane(page).locator('.console div').allTextContents();
    }

    ///
    ///It says what it is trying, before the answer comes back.
    ///
    ///An instrument with no SYSTem:HELP:HEADers? does not refuse it - it says nothing, and the
    ///query stands until the deadline. A console that printed nothing for those seconds would look
    ///like a button that had missed.
    ///
    test('names the query it is about to send', async ({ page }) => {
        await connect(page, instrument.address);
        await consoleButton(page, /Discover Commands/).click();

        await expect(pane(page).locator('.console'))
            .toContainText('--- discovering commands (SYSTem:HELP:HEADers?) ---');

        expect(instrument.asked(/HELP:HEAD/i).length).toBe(1);
    });

    ///
    ///An instrument that can answer is the better answer. No window: what it listed is what it
    ///has, and the catalog is a transcription of a guide for the model.
    ///
    test('prints what the instrument lists, and opens nothing', async ({ page, request }) => {
        const talkative = await startInstrument({ headers: HEADERS });
        try {
            await freshBench(page, request);
            await connect(page, talkative.address);
            await consoleButton(page, /Discover Commands/).click();

            await expect(pane(page).locator('.console'))
                .toContainText(`--- ${HEADERS.length} command headers reported by the instrument ---`);

            const log = (await logLines(page)).join('\n');
            for (const header of HEADERS) expect(log).toContain(header);

            await expect(page.locator('dialog.tool[open]')).toHaveCount(0);
        } finally {
            await talkative.stop();
        }
    });

    ///
    ///And one that cannot gets the catalog, said in as many words. The line matters: the window
    ///appearing is otherwise indistinguishable from a button that only ever opened a window.
    ///
    test('falls back to the bundled catalog, and says why', async ({ page }) => {
        await connect(page, instrument.address);
        await consoleButton(page, /Discover Commands/).click();

        await expect(pane(page).locator('.console'))
            .toContainText(/instrument doesn't support live discovery; opening the built-in reference/);
        await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText('Command Reference');
    });
});


test.describe('the shape of the box', () => {
    //Tall enough that there is height to give away. At the suite own 1000 the log stands at its 360
    //floor and the workspace scrolls, which is the other half of the rule rather than this one.
    test.use({ viewport: { width: 1400, height: 1300 } });

    ///
    ///The consoles box is the height of the window, as grpConsole is.
    ///
    ///MainForm anchors it on all four edges, so it stretches to whatever is left under the scan panel
    ///and the log grows with the window. Here the card was the size of what was in it: the log stood
    ///at its floor whatever the window did, and every pixel a taller window gave the page went into a
    ///field of nothing under the card - the one place on the page where the space below a card was
    ///not the space beside it.
    ///
    test('fills the window, and the space under it is the space beside it', async ({ page }) => {
        await connect(page, instrument.address);
        await settle(page);

        const log = await boxOf(page, '.pane:not([hidden]) .console');

        //Grown past the floor it used to stand at.
        expect(log.height).toBeGreaterThan(360);

        //And nothing left over between the card and the foot of the page.
        const dead = await page.evaluate(() => {
            const main = document.querySelector('main.workspace');
            const card = document.querySelector('.group.consoles');
            const pad = parseFloat(getComputedStyle(main).paddingBottom);
            return Math.round((main.getBoundingClientRect().bottom - pad
                               - card.getBoundingClientRect().bottom) * 10) / 10;
        });
        expect(dead).toBe(0);

        //The console's own card, measured out to the edge of the page each way. The arithmetic is the
        //nesting - card, pane, box, workspace - and the two readings have to come to the same number.
        const insets = await page.evaluate(() => {
            const R = (e) => e.getBoundingClientRect();
            const round = (n) => Math.round(n * 10) / 10;
            const main = document.querySelector('main.workspace');
            const card = document.querySelector('.pane:not([hidden]) .console').closest('.group');
            const pane = card.closest('.pane');
            const outer = card.closest('.group.consoles');
            const pad = parseFloat(getComputedStyle(main).paddingBottom);
            return {
                below: round((R(pane).bottom - R(card).bottom)
                             + (R(outer).bottom - R(pane).bottom) + pad),
                beside: round(R(card).left - R(main).left)
            };
        });
        expect(insets.below).toBe(insets.beside);
    });

    ///
    ///Which is not the same as squashing it on a short window: the log keeps its floor and the page
    ///scrolls instead.
    ///
    test("keeps the log floor on a window with no room to give", async ({ page }) => {
        await page.setViewportSize({ width: 1400, height: 700 });
        await connect(page, instrument.address);
        await settle(page);

        const log = await boxOf(page, '.pane:not([hidden]) .console');
        expect(log.height).toBe(360);

        const scrolls = await page.evaluate(() => {
            const main = document.querySelector('main.workspace');
            return main.scrollHeight > main.clientHeight;
        });
        expect(scrolls).toBe(true);
    });
});