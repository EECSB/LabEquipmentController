//
//The results panel: the table, the plot, and the controls that belong to one of them.
//
//Only a reply that is entirely one number is recorded - `*IDN?` and a comma-separated block are not -
//so the fake's measurement replies land here and its identity does not. That rule is Core's
//(RecordIfNumeric on the desktop), and the point of it is repetition: send the same query a few times
//and the drift is a line rather than a column of numbers to read down.
//
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane, quick, resultTab, boxOf } = require('./helpers');
const { startInstrument } = require('./instrument');

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

///Press DC V a few times and wait for the rows to arrive.
async function record(page, times) {
    for (let i = 0; i < times; i++) await quick(page, 'DC V').click();
    await expect(pane(page).locator('.split > .group:nth-child(2) tbody tr')).toHaveCount(times, { timeout: 20000 });
}

test.describe('the two panes', () => {
    ///
    ///Named as the desktop names them, near enough: its tabs are Results and Plot, and this one carries
    ///the word in the tab because it dropped the caption that used to stand over them.
    ///
    test('are Results table and Plot', async ({ page }) => {
        const tabs = pane(page).locator('.subtabs button');
        await expect(tabs).toHaveText(['Results table', 'Plot']);
    });

    ///
    ///Both start the same distance under the tab that selects them.
    ///
    ///Two panes of one control beginning at different heights reads as the second one having been
    ///bolted on, which is what a second top margin on the plot made it look like.
    ///
    test('start at the same height under the tabs', async ({ page }) => {
        await record(page, 2);

        const tabs = await boxOf(page, '.pane:not([hidden]) .subtabs');
        const table = await boxOf(page, '.pane:not([hidden]) .split > .group:nth-child(2) .scroll');
        const underTable = Math.round((table.top - tabs.bottom) * 10) / 10;

        await resultTab(page, 'Plot').click();
        const plot = await boxOf(page, '.pane:not([hidden]) .plot');
        const underPlot = Math.round((plot.top - tabs.bottom) * 10) / 10;

        expect(underPlot).toBeCloseTo(underTable, 0);
    });

    ///
    ///And crossing between them does not resize anything.
    ///
    ///The plot was 900×300 units stretched to the card's width, so its height was whatever that
    ///width happened to make it - a different height from the table beside it. Pressing Plot
    ///resized the card, and stretched the console in the same grid row with it.
    ///
    test('do not resize the card, or the one beside it', async ({ page }) => {
        await record(page, 2);

        const left = await boxOf(page, '.pane:not([hidden]) .split > .group:first-child');
        const right = await boxOf(page, '.pane:not([hidden]) .split > .group:nth-child(2)');

        await resultTab(page, 'Plot').click();

        const leftNow = await boxOf(page, '.pane:not([hidden]) .split > .group:first-child');
        const rightNow = await boxOf(page, '.pane:not([hidden]) .split > .group:nth-child(2)');

        expect(leftNow.width).toBeCloseTo(left.width, 0);
        expect(leftNow.height).toBeCloseTo(left.height, 0);
        expect(rightNow.width).toBeCloseTo(right.width, 0);
        expect(rightNow.height).toBeCloseTo(right.height, 0);
    });
});

test.describe('the table', () => {
    ///
    ///There before there is anything in it.
    ///
    ///Headings and all, which is what a ListView with its columns set shows from the moment the
    ///window opens - and the better thing to show, because the headings say what this console is
    ///going to record. A line of prose in its place meant the first reading did not arrive in the
    ///table: the table arrived, with the reading already in it, and everything under it moved.
    ///
    test('shows its headings before anything has been recorded', async ({ page }) => {
        const results = pane(page).locator('.split > .group:nth-child(2)');
        await expect(results.locator('thead th')).toHaveText(['Time', 'Command', 'Value']);
        await expect(results.locator('tbody tr')).toHaveCount(0);
    });

    ///
    ///Three columns, and a numeric reply in them.
    ///
    test('records a numeric reply under Time, Command and Value', async ({ page }) => {
        const results = pane(page).locator('.split > .group:nth-child(2)');

        //After recording, not before: the table is only drawn once there is a row for it, which is
        //itself the difference from the discovered-instruments list - that one keeps its headings.
        await record(page, 1);
        await expect(results.locator('thead th')).toHaveText(['Time', 'Command', 'Value']);

        const cells = results.locator('tbody tr td');
        await expect(cells.nth(1)).toHaveText('MEASure:VOLTage:DC?');
        await expect(cells.nth(2)).toHaveText(/^[+-]?\d/);
    });

    ///
    ///An identity is not a reading.
    ///
    ///A string in a value column is noise, and it would drag the plot's scale with it.
    ///
    test('an *IDN? reply is logged but not recorded', async ({ page }) => {
        await quick(page, '*IDN?').click();
        await expect(pane(page).locator('.console')).toContainText(instrument.identity);

        await expect(pane(page).locator('.split > .group:nth-child(2) tbody tr')).toHaveCount(0);
    });

    ///
    ///Save CSV and Clear Results act on the rows, so they belong under the rows.
    ///
    test('carries Save CSV and Clear Results, at the foot of the panel', async ({ page }) => {
        await record(page, 2);

        const results = pane(page).locator('.split > .group:nth-child(2)');
        await expect(results.getByRole('button', { name: 'Save CSV' })).toBeEnabled();
        await expect(results.getByRole('button', { name: 'Clear Results' })).toBeEnabled();

        //At the foot: the row sits against the panel's bottom padding rather than under whatever
        //happens to be above it.
        const panel = await boxOf(page, '.pane:not([hidden]) .split > .group:nth-child(2)');
        const foot = await boxOf(page, '.pane:not([hidden]) .split > .group:nth-child(2) .row.foot');
        expect(panel.bottom - foot.bottom).toBeLessThan(16);
    });

    ///
    ///Clear takes the rows and leaves the log.
    ///
    ///The log is the record of how those readings were produced, and throwing it away with them would
    ///lose the account of the measurement along with the measurement.
    ///
    test('Clear Results empties the table and leaves the log alone', async ({ page }) => {
        await record(page, 2);
        const results = pane(page).locator('.split > .group:nth-child(2)');

        await results.getByRole('button', { name: 'Clear Results' }).click();

        await expect(results.locator('tbody tr')).toHaveCount(0);
        await expect(pane(page).locator('.console')).toContainText('MEASure:VOLTage:DC?');
    });
});

test.describe('the plot', () => {
    ///
    ///The curve first, the pickers under it.
    ///
    ///They are what you reach for *after* looking at the plot - a wrong axis is something the picture
    ///tells you - and above it they pushed the picture down the panel and put a row of boxes between the
    ///tab you pressed and the thing it drew.
    ///
    test('draws the curve above its pickers', async ({ page }) => {
        await record(page, 3);
        await resultTab(page, 'Plot').click();

        const canvas = await boxOf(page, '.pane:not([hidden]) .plotcanvas');
        const pickers = await boxOf(page, '.pane:not([hidden]) .plotpick');

        expect(pickers.top).toBeGreaterThanOrEqual(canvas.bottom - 1);
    });

    ///
    ///And the picture is worth keeping on its own, which is what the camera is for.
    ///
    test('offers the axes, the log toggles and a camera', async ({ page }) => {
        await record(page, 3);
        await resultTab(page, 'Plot').click();

        const pick = pane(page).locator('.plotpick');
        await expect(pick).toContainText('X:');
        await expect(pick).toContainText('X Unit:');
        await expect(pick).toContainText('Log X');
        await expect(pick).toContainText('Log Y');
        await expect(pick).toContainText('Points');
        await expect(pick.getByRole('button', { name: 'Save plot' })).toBeVisible();
    });

    ///
    ///Save CSV and Clear Results are not here.
    ///
    ///They act on the rows, and under the plot they stood beside its own camera button - which saves
    ///the same readings as a picture. A corner offering two Saves meaning two different things.
    ///
    test('does not repeat the table controls', async ({ page }) => {
        await record(page, 2);
        await resultTab(page, 'Plot').click();

        const results = pane(page).locator('.split > .group:nth-child(2)');
        await expect(results.getByRole('button', { name: 'Save CSV' })).toHaveCount(0);
        await expect(results.getByRole('button', { name: 'Clear Results' })).toHaveCount(0);
    });

    ///
    ///Nor a count of what is already on screen.
    ///
    test('does not caption the panel with a reading count', async ({ page }) => {
        await record(page, 2);
        const results = pane(page).locator('.split > .group:nth-child(2)');

        await expect(results).not.toContainText('readings');
        await resultTab(page, 'Plot').click();
        await expect(results).not.toContainText('readings');
    });

    ///
    ///Drawn at the size of the box it was given, one unit to the pixel.
    ///
    ///ResultPlotPanel fills its tab page and paints at whatever size that is. This measures the
    ///box and draws in its units, which is the same thing and the reason the two tabs are now the
    ///same height: an aspect ratio of its own is what made them differ.
    ///
    test('draws in the units of its own box', async ({ page }) => {
        await record(page, 3);
        await resultTab(page, 'Plot').click();

        const canvas = await boxOf(page, '.pane:not([hidden]) .plotcanvas');
        const viewBox = await pane(page).locator('.plotcanvas').getAttribute('viewBox');
        const [w, h] = viewBox.split(' ').slice(2).map(Number);

        expect(Math.abs(w - canvas.width)).toBeLessThan(1.5);
        expect(Math.abs(h - canvas.height)).toBeLessThan(1.5);
    });
});
