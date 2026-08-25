//
//The command queue, and the thing it exists for.
//
//A connection carries one conversation at a time, so a second command pressed while one is in flight
//has to wait. The console used to grey out its quick commands and Send until the reply came back, which
//made the queue unreachable: there was no way to put a second command into a strip whose whole purpose
//is to hold one. That was reported as "I cannot click a button until a request is received back", and
//it is the reason this file exists.
//
//The desktop locks the console only while a *script* or a *live readout* is driving the link
//(Session.IsBusy, set in exactly those two places). One exchange is not that.
//
//The instrument here is deliberately slow. A queue is a thing that only exists while something is still
//being answered, so a fake that replied instantly would leave nothing to see.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, quick, queueChips, consoleButton, boxOf } = require('./helpers');
const { startInstrument } = require('./instrument');

///Long enough that five presses land while the first is still being answered, short enough that the
///spec does not spend its budget waiting for the queue to drain.
const REPLY_MS = 700;

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument({ delayMs: REPLY_MS });
});

test.afterAll(async () => {
    await instrument.stop();
});

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, instrument.address);
    instrument.received.length = 0;
});

test.describe('the queue strip', () => {
    ///
    ///Drawn whether or not anything is in it.
    ///
    ///An outline that appeared with the first command and vanished with the last would read as a thing
    ///arriving rather than as the place where things arrive - and would shove the log down and up again
    ///on every press.
    ///
    test('keeps its box and its height with nothing queued', async ({ page }) => {
        const strip = pane(page).locator('.queue');

        await expect(strip).toBeVisible();
        await expect(queueChips(page)).toHaveCount(0);

        //And it says what it is. An empty outline is not self-explanatory: a box with nothing in it and
        //no word on it is one you have to see fill up once before you know what it was for.
        await expect(strip).toHaveText('Command queue');

        //As tall as a chip and no taller, but never nothing — the height must not change when the first
        //command arrives, or the log below it would jump on every press.
        const box = await boxOf(page, '.pane:not([hidden]) .queue');
        expect(box.height).toBeGreaterThan(20);
        expect(await page.evaluate(() =>
            getComputedStyle(document.querySelector('.pane:not([hidden]) .queue')).borderTopWidth)).toBe('1px');
    });

    ///
    ///The word naming an empty strip is inset. A chip is not.
    ///
    ///The chip is what the box is there to hold, and a gap round it reads as a border with air
    ///inside it — which is why the strip carries no padding of its own. The word is not a chip,
    ///and pressed into the corner it looked like a mistake; it takes a chip's own inset instead,
    ///so it stands exactly where the first chip's text will stand.
    ///
    test('insets the word, and nothing else', async ({ page }) => {
        //Where the text starts, measured from the strip: the element, plus its border, plus its
        //padding. The two have to agree.
        const startOf = (sel) => page.evaluate((s) => {
            const strip = document.querySelector('.pane:not([hidden]) .queue');
            const el = strip.querySelector(s);
            if (!el) return null;
            const cs = getComputedStyle(el);
            return el.getBoundingClientRect().left - strip.getBoundingClientRect().left
                 + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft);
        }, sel);

        const word = await startOf('.idle');
        expect(word).toBeGreaterThan(3);

        const dcv = quick(page, 'DC V');
        for (let i = 0; i < 4; i++) await dcv.click();
        await expect.poll(async () => await queueChips(page).count()).toBeGreaterThanOrEqual(1);

        //The chip itself against the border, its text where the word was.
        const chip = await page.evaluate(() => {
            const strip = document.querySelector('.pane:not([hidden]) .queue');
            const el = strip.querySelector('.chip');
            return el.getBoundingClientRect().left - strip.getBoundingClientRect().left;
        });
        expect(chip).toBeLessThan(2);
        expect(await startOf('.chip')).toBeCloseTo(word, 0);
    });

    ///
    ///It sits above the log, where the desktop's QueueStrip sits.
    ///
    test('stands between the tools and the log', async ({ page }) => {
        const strip = await boxOf(page, '.pane:not([hidden]) .queue');
        const log = await boxOf(page, '.pane:not([hidden]) .console');

        expect(strip.bottom).toBeLessThanOrEqual(log.top);
    });
});

test.describe('pressing while a command is in flight', () => {
    ///
    ///**Nothing is greyed out.** The whole point.
    ///
    ///Five presses in a row, while the first is still being answered: every one is accepted, no button
    ///goes dead, and the instrument is asked five times.
    ///
    test('five presses in a row all land, and no button goes dead', async ({ page }) => {
        const dcv = quick(page, 'DC V');

        for (let i = 0; i < 5; i++) await dcv.click();

        //Checked while the queue is still deep rather than afterwards: "was it ever disabled" is the
        //question, and after the last reply the answer is no whatever happened in between.
        await expect(queueChips(page)).not.toHaveCount(0);

        const anyDead = await page.evaluate(() =>
            [...document.querySelectorAll('.pane:not([hidden]) button.quick')].some((b) => b.disabled));
        expect(anyDead, 'a quick command went dead while a command was in flight').toBe(false);

        await expect.poll(() => instrument.asked(/MEASure:VOLTage:DC\?/).length, { timeout: 20000 }).toBe(5);
    });

    ///
    ///And they queue rather than interleaving: one chip each, in the order they were pressed, with the
    ///arrow between them saying which is next.
    ///
    test('the presses stack up as chips, oldest first', async ({ page }) => {
        const dcv = quick(page, 'DC V');
        for (let i = 0; i < 4; i++) await dcv.click();

        await expect.poll(async () => await queueChips(page).count()).toBeGreaterThanOrEqual(2);

        const chips = await queueChips(page).allTextContents();
        for (const c of chips) expect(c.trim()).toBe('MEASure:VOLTage:DC?');

        //The arrow is drawn between chips and not before the first.
        const arrows = await pane(page).locator('.queue .to').count();
        expect(arrows).toBe(chips.length - 1);
    });

    ///
    ///Send stays live too, so a typed command can join a queue a quick command started.
    ///
    test('Send accepts a command while the strip is busy', async ({ page }) => {
        const dcv = quick(page, 'DC V');
        for (let i = 0; i < 3; i++) await dcv.click();

        const box = pane(page).locator('.row.cmd input');
        await box.fill('MEASure:RESistance?');

        await expect(consoleButton(page, 'Send')).toBeEnabled();
        await consoleButton(page, 'Send').click();

        await expect.poll(() => instrument.asked(/MEASure:RESistance\?/).length, { timeout: 20000 }).toBe(1);
    });

    ///
    ///The queue empties itself. A strip that filled and stayed full would be a leak with a border round
    ///it, and it is fed by an event rather than by polling - so a missed event looks exactly like this.
    ///
    test('the strip drains once the instrument has answered', async ({ page }) => {
        const dcv = quick(page, 'DC V');
        for (let i = 0; i < 3; i++) await dcv.click();

        await expect(queueChips(page)).toHaveCount(0, { timeout: 25000 });

        //And every command is accounted for in the log, so nothing was dropped on the way out.
        const log = await pane(page).locator('.console').textContent();
        expect(log.match(/> MEASure:VOLTage:DC\?/g)).toHaveLength(3);
    });
});
