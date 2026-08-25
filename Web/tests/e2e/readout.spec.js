//
//The live readout: poll one measurement on a timer and plot it against time.
//
//A meter answers one reading per query, so the only way to see a trend - a drifting supply, a warming
//thermistor, a settling reference - is to ask repeatedly. A REPEAT script can do the asking, but its
//answers scroll past as text; this plots them.
//
//The asking is done by the *server*. A page that is scrolled, backgrounded or on a slow link would
//otherwise be the thing deciding the sample interval, and the interval is the measurement. So what the
//specs here watch for is readings arriving without the browser having asked for each one.
//
//The fake ramps its replies rather than repeating a number, which is what makes the plot a line and
//lets a spec tell one reading from the next.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, boxOf, rowGaps } = require('./helpers');
const { startInstrument } = require('./instrument');

///Fast enough that a spec sees several readings without waiting on them, and still above the box's own
///floor of 100.
const EVERY_MS = 200;

let instrument;

test.beforeAll(async () => {
    instrument = await startInstrument();
});

test.afterAll(async () => {
    await instrument.stop();
});

///The readout window, open over a connected console.
async function openReadout(page) {
    await pane(page).getByRole('button', { name: /Live Readout/ }).click();
    await expect(page.locator('dialog.tool[open]')).toBeVisible();
    await expect(page.locator('dialog.tool[open] > .tool-head')).toContainText('Readout');
    return page.locator('dialog.tool[open]');
}

///Set the interval, which is bound on change rather than on input.
async function setInterval(page, ms) {
    await page.locator('dialog.tool[open] #every').fill(String(ms));
    await page.locator('dialog.tool[open] #every').blur();
}

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, instrument.address);
    instrument.received.length = 0;
});

test.describe('the control row', () => {
    ///
    ///What to measure, what to show it in, how often, and the thing that starts it - in that order.
    ///
    test('asks what, in what units, and how often, then offers Start', async ({ page }) => {
        const tool = await openReadout(page);

        await expect(tool).toContainText('Measure:');
        await expect(tool).toContainText('shown in');
        await expect(tool).toContainText('every');
        await expect(tool).toContainText('ms');
        await expect(tool.getByRole('button', { name: 'Start' })).toBeEnabled();
    });

    ///
    ///The measurements are the profile's, not a fixed list.
    ///
    ///A multimeter documents eight readout functions; an instrument with none has this button greyed
    ///out on the console and never gets here at all.
    ///
    test('the measurement picker holds the profile own readouts', async ({ page }) => {
        const tool = await openReadout(page);
        const options = tool.locator('#fn option');

        await expect(options).toHaveCount(8);
        await expect(options.first()).toHaveText('DC volts');
    });

    ///
    ///Auto first, Raw last, and the prefixes in between: two of the entries are not prefixes at all.
    ///Auto picks one from the size of the reading, and Raw prints the number as the instrument sent it -
    ///which is the one to use when you suspect the scaling rather than the instrument.
    ///
    test('the unit picker runs from Auto to Raw', async ({ page }) => {
        const tool = await openReadout(page);
        const scales = tool.locator('#scale option');

        await expect(scales.first()).toHaveText('Auto');
        await expect(scales.last()).toHaveText('Raw');
    });

    ///
    ///The interval's own numbers: a tenth of a second to a minute, in tenths, starting at one second.
    ///
    test('the interval box carries its bounds', async ({ page }) => {
        const tool = await openReadout(page);
        const every = tool.locator('#every');

        await expect(every).toHaveValue('1000');
        await expect(every).toHaveAttribute('min', '100');
        await expect(every).toHaveAttribute('max', '60000');
        await expect(every).toHaveAttribute('step', '100');
    });

    ///
    ///Start sits apart from the fields, not butted up against the last of them.
    ///
    ///Everything to the left says what to measure and how often; this is the thing that starts it.
    ///Against the box it would read as part of the field.
    ///
    test('Start stands clear of the interval box', async ({ page }) => {
        await openReadout(page);

        //Across the spacer, not merely across the row's own gap: the step is the spacer plus the gap
        //either side of it, and measuring one adjacent pair would report the gap and miss the step.
        const step = await page.evaluate(() => {
            const spacer = document.querySelector('dialog.tool[open] .row .gap');
            const before = spacer.previousElementSibling.getBoundingClientRect();
            const after = spacer.nextElementSibling.getBoundingClientRect();
            return Math.round((after.left - before.right) * 10) / 10;
        });

        expect(step).toBeGreaterThan(25);
    });
});

test.describe('polling', () => {
    ///
    ///Start asks the instrument over and over, and the readings arrive on their own.
    ///
    ///The browser sends nothing per reading - the server holds the socket and the timer. What proves it
    ///is the instrument's own tally climbing while the page merely watches.
    ///
    test('the server does the asking, and the readings keep coming', async ({ page }) => {
        const tool = await openReadout(page);
        await setInterval(page, EVERY_MS);
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect.poll(() => instrument.asked(/MEASure:VOLTage:DC\?/).length, { timeout: 20000 })
            .toBeGreaterThan(3);

        //And the big number is showing one of them.
        await expect(tool.locator('.readout')).toHaveText(/\d/);

        await tool.getByRole('button', { name: 'Stop' }).click();
        await expect(tool.getByRole('button', { name: 'Start' })).toBeVisible();
    });

    ///
    ///While it runs, what it is measuring and how often cannot be changed under it.
    ///
    ///Both would change the run rather than the next one, and a curve whose axis changed halfway is not
    ///a measurement of anything.
    ///
    test('the measurement and the interval are held while it runs', async ({ page }) => {
        const tool = await openReadout(page);
        await setInterval(page, EVERY_MS);
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect(tool.locator('#fn')).toBeDisabled();
        await expect(tool.locator('#every')).toBeDisabled();

        //The unit picker is not: it changes how the same reading is shown, not what is being read.
        await expect(tool.locator('#scale')).toBeEnabled();

        await tool.getByRole('button', { name: 'Stop' }).click();
        await expect(tool.locator('#fn')).toBeEnabled({ timeout: 20000 });
        await expect(tool.locator('#every')).toBeEnabled();
    });

    ///
    ///Stopping keeps what was taken, which is what its tooltip promises.
    ///
    test('Stop keeps the readings it has', async ({ page }) => {
        const tool = await openReadout(page);
        await setInterval(page, EVERY_MS);
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect.poll(() => instrument.asked(/MEASure:VOLTage:DC\?/).length, { timeout: 20000 })
            .toBeGreaterThan(2);

        await tool.getByRole('button', { name: 'Stop' }).click();

        await expect(tool.getByRole('button', { name: 'Save CSV' })).toBeEnabled();
        await expect(tool.getByRole('button', { name: 'Clear' })).toBeEnabled();
        await expect(tool.locator('.readout')).toHaveText(/\d/);
    });

    ///
    ///And the readings are a curve, not a column of numbers to read down. That is the whole point of
    ///the window.
    ///
    test('the readings are plotted against time', async ({ page }) => {
        const tool = await openReadout(page);
        await setInterval(page, EVERY_MS);
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect.poll(() => instrument.asked(/MEASure:VOLTage:DC\?/).length, { timeout: 20000 })
            .toBeGreaterThan(3);
        await tool.getByRole('button', { name: 'Stop' }).click();

        //Seconds across and the value up - the two columns the desktop's window names.
        const plot = tool.locator('.plot');
        await expect(plot).toContainText('Seconds');

        //A path with points on it, rather than the empty-state message.
        await expect(tool.locator('.plotcanvas path')).not.toHaveCount(0);
    });
});

test.describe('what it keeps', () => {
    ///
    ///Nothing to clear or save before there is anything to clear or save.
    ///
    test('Clear and Save CSV are dead before a run', async ({ page }) => {
        const tool = await openReadout(page);

        await expect(tool.getByRole('button', { name: 'Clear' })).toBeDisabled();
        await expect(tool.getByRole('button', { name: 'Save CSV' })).toBeDisabled();
    });

    ///
    ///Clear starts the run again from nothing - the readings and the curve together, because they are
    ///the same readings.
    ///
    test('Clear empties the readings and the curve', async ({ page }) => {
        const tool = await openReadout(page);
        await setInterval(page, EVERY_MS);
        await tool.getByRole('button', { name: 'Start' }).click();

        await expect.poll(() => instrument.asked(/MEASure:VOLTage:DC\?/).length, { timeout: 20000 })
            .toBeGreaterThan(2);
        await tool.getByRole('button', { name: 'Stop' }).click();
        await expect(tool.getByRole('button', { name: 'Clear' })).toBeEnabled();

        await tool.getByRole('button', { name: 'Clear' }).click();

        await expect(tool.getByRole('button', { name: 'Clear' })).toBeDisabled();
        await expect(tool.getByRole('button', { name: 'Save CSV' })).toBeDisabled();
        await expect(tool.locator('.plotcanvas')).toContainText('Nothing recorded yet');
    });

    ///
    ///The value is large on purpose: the point of a meter readout is being able to read it from where
    ///the probes are rather than from the chair.
    ///
    test('the reading is shown large', async ({ page }) => {
        const tool = await openReadout(page);
        await setInterval(page, EVERY_MS);
        await tool.getByRole('button', { name: 'Start' }).click();
        await expect(tool.locator('.readout')).toHaveText(/\d/, { timeout: 20000 });
        await tool.getByRole('button', { name: 'Stop' }).click();

        const size = await page.evaluate(() =>
            parseFloat(getComputedStyle(document.querySelector('dialog.tool[open] .readout')).fontSize));

        //Several times the page's own text, which is 13px.
        expect(size).toBeGreaterThan(30);
    });
});

test.describe('the shape of the window', () => {
    ///
    ///One card filling the frame, as MultimeterReadoutForm is docked: a toolbar on top, the value and
    ///its figures under it, the trend filling whatever is left, and the actions along the bottom. It
    ///was a card of its own height sitting in the top of a window, with the rest of the frame empty
    ///below it and the plot on a fixed floor regardless of how much room there was.
    ///
    test('is one card, and the plot takes what the fixed rows leave', async ({ page }) => {
        const tool = await openReadout(page);

        await expect(tool.locator('.tool-body.fill')).toHaveCount(1);

        const body = await boxOf(page, 'dialog.tool[open] .tool-body');
        const card = await boxOf(page, 'dialog.tool[open] .group.tall');
        const plot = await boxOf(page, 'dialog.tool[open] .plotbox');

        //The card is the body, less the body's own padding on each side.
        expect(body.bottom - card.bottom).toBeLessThan(20);
        expect(card.height / body.height).toBeGreaterThan(0.9);

        //And the plot has the rest of it: everything under the figures, down to the picker strip.
        const strip = await boxOf(page, 'dialog.tool[open] .row.plotpick');
        expect(plot.bottom).toBeLessThanOrEqual(strip.top);
        expect(plot.height).toBeGreaterThan(150);
        expect(plot.width).toBeGreaterThan(card.width - 40);
    });

    ///
    ///No caption over it. The title bar says Readout and says which instrument as well, and a word
    ///inside the frame repeating the word in the frame's own title bar says nothing twice.
    ///
    test('is not captioned as well as titled', async ({ page }) => {
        const tool = await openReadout(page);

        await expect(tool.locator('.tool-head')).toContainText('Readout');
        await expect(tool.locator('.group > .cap')).toHaveCount(0);
    });

    ///
    ///Clear and Save CSV... go at the end of the picker strip, beside the one that saves the picture.
    ///MultimeterReadoutForm has one row of actions along the foot of its window; a second row of two
    ///buttons under a row that already ends in empty space is a row spent saying nothing.
    ///
    test('puts Clear and Save CSV beside the button that saves the picture', async ({ page }) => {
        await openReadout(page);

        const feet = await page.evaluate(() =>
            [...document.querySelectorAll('dialog.tool[open] .row.plotpick > button')].map((b) => ({
                label: b.textContent.trim() || b.getAttribute('aria-label'),
                top: Math.round(b.getBoundingClientRect().top),
                right: Math.round(b.getBoundingClientRect().right)
            })));

        expect(feet.map((f) => f.label)).toEqual(['Clear', 'Save CSV…', 'Save plot']);

        //One line, and hard against the right edge of the strip.
        expect(new Set(feet.map((f) => f.top)).size).toBe(1);
        const strip = await boxOf(page, 'dialog.tool[open] .row.plotpick');
        expect(strip.right - feet[2].right).toBeLessThan(2);
    });
});

test.describe('the control row\'s distances', () => {
    ///
    ///MultimeterReadoutForm states its own three, and they are not MainForm\'s two-eight-fifty-two:
    ///six pixels between a label and the box it names, fourteen between one pair and the next, four
    ///before "ms" - which belongs to the number in front of it rather than being the next thing along
    ///- and twenty-six before Start.
    ///
    test('are the desktop\'s six, fourteen, four and twenty-six', async ({ page }) => {
        await openReadout(page);

        const gaps = await rowGaps(page, 'dialog.tool[open] .row.meterrow');
        expect(gaps.map((g) => g.px)).toEqual([6, 14, 6, 14, 6, 4, 6, 6]);

        //The last two are either side of the spacer before Start, which is 14 wide: 6 + 14 + 6.
        const spacer = await boxOf(page, 'dialog.tool[open] .row.meterrow .gap');
        expect(gaps[6].px + spacer.width + gaps[7].px).toBe(26);
    });
});
