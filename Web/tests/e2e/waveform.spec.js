//
//The waveform window, over a scope that answers.
//
//The capture itself is Core's, and its arithmetic has an xUnit suite of its own: each dialect's
//scaling, and a channel the scope does not have. What is checked here is the window - the cards and
//what each one draws, the figures under them, the legend, the gestures and the file Save CSV writes -
//against a fake two-channel Rigol speaking the :WAVeform tree (instrument.js). Nothing in the suite
//could open this window with a trace in it before: the meter the other specs talk to has no waveform
//to give.
//
//The fake's channel 1 is a sine of 2 V peak to peak and its channel 2 a square of 1 V, both across
//1,400 points a microsecond apart; a third channel is answered the way a real DS2202 answers one it
//does not have.
//
const fs = require('fs');
const { test, expect } = require('./fixtures');
const { freshBench, connect, pane } = require('./helpers');
const { startInstrument } = require('./instrument');

///A two-channel Rigol. The serial says what it is.
const SCOPE = 'RIGOL TECHNOLOGIES,DS2202,LEC-E2E-0003,00.01.00';

///The colours the window draws channels 1 and 2 in: the ones printed beside the scope's own BNCs.
const YELLOW = '#f5d90a';
const CYAN = '#22d3ee';

let scope;

test.beforeAll(async () => {
    scope = await startInstrument({ identity: SCOPE, channels: 2 });
});

test.afterAll(async () => {
    await scope.stop();
});

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
    await connect(page, scope.address);
    scope.received.length = 0;
});

///The window, over the scope's console. It opens with one card, on channel 1, and nothing captured.
async function openWaveform(page) {
    await pane(page).getByRole('button', { name: 'Capture Waveform' }).click();
    const win = page.locator('dialog.tool[open]');
    await expect(win.locator('> .tool-head')).toContainText('Waveform');
    return win;
}

///The cards, in the order they were added.
const cards = (win) => win.locator('.pane');

///One card's switch for one channel.
const chip = (card, n) => card.locator(`label.chip.ch${n}`);

///The press at the top of the window, which fills every card from one capture.
async function capture(win) {
    await win.locator('.waverow').first().getByRole('button', { name: 'Capture Waveform' }).click();
    await expect(win.locator('.spin')).toHaveCount(0);
}

///What a card's foot says about its capture, with the markup's line breaks read as the spaces they show as.
async function footOf(card) {
    return ((await card.locator('.foot .muted').first().textContent()) ?? '').replace(/\s+/g, ' ').trim();
}

///The line under a trace saying what part of the record is drawn, and how to move it.
const said = (card) => card.locator('svg.trace text').last();

///The time at the left-hand end of what a card draws, as the number it prints.
async function fromOf(card) {
    const words = await card.locator('svg.trace text').evaluateAll(
        (ts) => ts.map((t) => t.textContent).filter((w) => / s$/.test(w)));
    return Number(words[0].replace(/ s$/, ''));
}

///How tall a drawn path stands, in the picture's own units: its lowest point to its highest.
async function heightsOf(card) {
    return card.locator('svg.trace path').evaluateAll((paths) => paths.map((p) => {
        const ys = p.getAttribute('d').split(/[ML]/).filter((s) => s.trim()).map((s) => Number(s.trim().split(' ')[1]));
        return Math.max(...ys) - Math.min(...ys);
    }));
}

test.describe('the waveform window', () => {
    ///
    ///One card, one channel: the trace in that channel's colour, and under it the figures that are
    ///facts about the capture - how many points, the peak to peak, the span of the record. One read,
    ///of the one channel asked for.
    ///
    test('draws channel 1 and says what was measured', async ({ page }) => {
        const win = await openWaveform(page);
        await expect(cards(win)).toHaveCount(1);
        await expect(cards(win).first()).toContainText('Nothing on this card yet');

        await capture(win);
        const card = cards(win).first();
        await expect(card.locator('svg.trace path')).toHaveCount(1);
        await expect(card.locator('svg.trace path')).toHaveAttribute('stroke', YELLOW);
        await expect.poll(() => footOf(card)).toBe('1,400 points · CH1 Vpp 2 V · span 1.399e-3 s');

        expect(scope.asked(/WAV(eform)?:SOUR/i)).toEqual([':WAVeform:SOURce CHANnel1']);
    });

    ///
    ///Two channels on one card are read against each other: one vertical scale across both, so the
    ///1 V square stands half as tall as the 2 V sine rather than filling the card the same way. And a
    ///legend, where the colours are, naming which is which.
    ///
    test('puts two channels on one scale, with a legend and a figure each', async ({ page }) => {
        const win = await openWaveform(page);
        const card = cards(win).first();
        await chip(card, 2).locator('input').check();
        await capture(win);

        const paths = card.locator('svg.trace path');
        await expect(paths).toHaveCount(2);
        expect(await paths.evaluateAll((ps) => ps.map((p) => p.getAttribute('stroke')))).toEqual([YELLOW, CYAN]);

        const legend = await card.locator('svg.trace text').evaluateAll(
            (ts) => ts.map((t) => [t.textContent, t.getAttribute('fill')]));
        expect(legend).toEqual(expect.arrayContaining([['CH1', YELLOW], ['CH2', CYAN]]));

        await expect.poll(() => footOf(card)).toBe('1,400 points · CH1 Vpp 2 V · CH2 Vpp 1 V · span 1.399e-3 s');

        const [sine, square] = await heightsOf(card);
        expect(square / sine).toBeCloseTo(0.5, 1);
    });

    ///
    ///A second card lands on the lowest channel no card is showing, and draws it on its own terms: its
    ///own scale, so the square fills its card as the sine fills the first. Both come out of one
    ///capture - each channel is read once however many cards draw it.
    ///
    test('adds a card on the next channel, drawn from the same capture', async ({ page }) => {
        const win = await openWaveform(page);
        await win.getByRole('button', { name: /Add channel capture/ }).click();
        await expect(cards(win)).toHaveCount(2);
        await expect(chip(cards(win).nth(1), 2)).toHaveClass(/\bon\b/);
        await expect(chip(cards(win).nth(1), 1)).not.toHaveClass(/\bon\b/);

        await capture(win);
        const [one, two] = [cards(win).nth(0), cards(win).nth(1)];
        await expect(one.locator('svg.trace path')).toHaveAttribute('stroke', YELLOW);
        await expect(two.locator('svg.trace path')).toHaveAttribute('stroke', CYAN);
        await expect.poll(() => footOf(two)).toBe('1,400 points · CH2 Vpp 1 V · span 1.399e-3 s');

        const [sine] = await heightsOf(one);
        const [square] = await heightsOf(two);
        expect(square / sine).toBeCloseTo(1, 1);

        expect(scope.asked(/WAV(eform)?:SOUR/i)).toEqual([':WAVeform:SOURce CHANnel1', ':WAVeform:SOURce CHANnel2']);
    });

    ///
    ///A channel the scope does not have fails on its own, and says why where its figure would be. The
    ///others are still drawn. The scope did not refuse: it answered with two samples, against fourteen
    ///hundred on the time base, which is not a short trace but no trace.
    ///
    test('says so of a channel the scope does not have, and still draws the rest', async ({ page }) => {
        const win = await openWaveform(page);
        const card = cards(win).first();
        await chip(card, 3).locator('input').check();
        await capture(win);

        await expect(card.locator('svg.trace path')).toHaveCount(1);
        await expect(card.locator('.foot .error'))
            .toContainText('CH3: answered with 2 samples against 1,400 on the time base');
        await expect.poll(() => footOf(card)).toBe('1,400 points · CH1 Vpp 2 V · span 1.399e-3 s');
    });

    ///
    ///The gestures, on the card they are made on and nowhere else. The buttons zoom about the middle;
    ///Ctrl and the wheel about the pointer; the wheel alone slides the view, and so does a drag; a
    ///double-click puts the whole record back. The line under the trace says how much is drawn, and
    ///the peak to peak stays the record's however far in the view goes.
    ///
    test('zooms and pans one card at a time, and puts the whole record back', async ({ page }) => {
        const win = await openWaveform(page);
        await win.getByRole('button', { name: /Add channel capture/ }).click();
        await capture(win);
        const [one, two] = [cards(win).nth(0), cards(win).nth(1)];
        await expect(said(one)).toHaveText(/^whole record/);
        await expect(one.getByRole('button', { name: 'Zoom out' })).toBeDisabled();
        const whole = await fromOf(one);

        await one.getByRole('button', { name: 'Zoom in' }).click();
        await expect(said(one)).toHaveText(/^80\.0\s?% of the record/);
        await expect(said(two)).toHaveText(/^whole record/);
        await expect(one.getByRole('button', { name: 'Zoom out' })).toBeEnabled();
        const zoomed = await fromOf(one);
        expect(zoomed).toBeGreaterThan(whole);
        await expect.poll(() => footOf(one)).toBe('1,400 points · CH1 Vpp 2 V · span 1.399e-3 s');

        //The wheel alone, away from the hand: later in the record.
        const trace = one.locator('svg.trace');
        await trace.hover();
        await page.mouse.wheel(0, 120);
        await expect.poll(() => fromOf(one)).toBeGreaterThan(zoomed);

        //A drag to the right moves the trace with the hand: earlier.
        const later = await fromOf(one);
        const box = await trace.boundingBox();
        await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width / 2 + 150, box.y + box.height / 2, { steps: 6 });
        await page.mouse.up();
        await expect.poll(() => fromOf(one)).toBeLessThan(later);

        //Ctrl and the wheel, on the other card: it zooms, and the first is left where it was.
        await two.locator('svg.trace').hover();
        await page.keyboard.down('Control');
        await page.mouse.wheel(0, -120);
        await page.keyboard.up('Control');
        await expect(said(two)).toHaveText(/^80\.0\s?% of the record/);

        //A double-click on the first, and it is the whole record again.
        await trace.dblclick();
        await expect(said(one)).toHaveText(/^whole record/);
        expect(await fromOf(one)).toBe(whole);
        await expect(said(two)).toHaveText(/^80\.0\s?% of the record/);

        //And the button that does the same, on the second.
        await two.getByRole('button', { name: 'Show the whole record' }).click();
        await expect(said(two)).toHaveText(/^whole record/);
    });

    ///
    ///Save CSV is the card's channels, the whole record however far in the view is: one time column,
    ///then a column of volts per channel. The file is named for the channels, because with several
    ///cards open the alternative is four files called waveform.
    ///
    test('saves the card as CSV, the whole record, a column per channel', async ({ page }) => {
        const win = await openWaveform(page);
        const card = cards(win).first();
        await chip(card, 2).locator('input').check();
        await capture(win);
        await card.getByRole('button', { name: 'Zoom in' }).click();

        const [file] = await Promise.all([
            page.waitForEvent('download'),
            card.getByRole('button', { name: /Save CSV/ }).click(),
        ]);
        expect(file.suggestedFilename()).toBe('waveform-ch1-2.csv');

        const rows = fs.readFileSync(await file.path(), 'utf8').trim().split(/\r?\n/);
        expect(rows[0]).toBe('Time (s),CH1 (V),CH2 (V)');
        expect(rows).toHaveLength(1 + 1400);

        //The first sample: the record's start, the sine at zero, the square on its high half.
        const [time, sine, square] = rows[1].split(',').map(Number);
        expect(time).toBeCloseTo(-7e-4, 12);
        expect(sine).toBeCloseTo(0, 9);
        expect(square).toBeCloseTo(0.5, 9);
    });

    ///
    ///Run keeps capturing on a timer until it is pressed again, and its glyph says which it is doing.
    ///
    test('keeps capturing while Run is on, and stops when it is pressed again', async ({ page }) => {
        const win = await openWaveform(page);
        const row = win.locator('.waverow').first();
        await row.locator('input.num').fill('100');
        await row.locator('input.num').blur();   // the box is read when it is left

        await row.getByRole('button', { name: 'Run' }).click();
        await expect.poll(() => scope.asked(/WAV(eform)?:DATA\?/i).length, { timeout: 15000 }).toBeGreaterThanOrEqual(3);
        await expect(cards(win).first().locator('svg.trace path')).toHaveCount(1);

        await row.getByRole('button', { name: 'Run' }).click();
        const stopped = scope.asked(/WAV(eform)?:DATA\?/i).length;
        await page.waitForTimeout(600);
        expect(scope.asked(/WAV(eform)?:DATA\?/i).length).toBeLessThanOrEqual(stopped + 1);
    });

    ///
    ///The last card can go, and what is left is the frame that puts one back - with nothing to
    ///capture into, Capture Waveform is greyed. Four cards is one per channel, which is as far apart
    ///as they go.
    ///
    test('lets the last card go, and stops at four', async ({ page }) => {
        const win = await openWaveform(page);
        const add = win.getByRole('button', { name: /Add channel capture/ });

        await cards(win).first().getByRole('button', { name: 'Close this capture' }).click();
        await expect(cards(win)).toHaveCount(0);
        await expect(win.locator('.waverow').first().getByRole('button', { name: 'Capture Waveform' })).toBeDisabled();

        for (let n = 1; n <= 4; n++) {
            await add.click();
            await expect(chip(cards(win).nth(n - 1), n)).toHaveClass(/\bon\b/);
        }
        await expect(cards(win)).toHaveCount(4);
        await expect(add).toBeDisabled();
    });
});
