//
//The numbers in UI-SPEC §1, read back off the running page.
//
//These are the assertions the port did not have, and their absence is why the same class of finding
//came back over and over: a control a size out, a glyph a third too big, a gap in the wrong place. A
//number is not something to look at - it is something to measure, and the browser will say what it
//actually did.
//
//Tolerances are a pixel or two throughout. Sub-pixel layout is real and a card's border is a whole
//pixel of it; an assertion tight enough to fail on that would be measuring the renderer rather than
//the stylesheet.
//
const { test, expect } = require('./fixtures');
const { freshBench, boxOf, styleOf } = require('./helpers');

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
});

test.describe('control metrics', () => {
    ///
    ///One height, for every control standing on a row.
    ///
    ///The desktop measures a probe button carrying a glyph and pins everything to it - fighting
    ///ComboBox through ItemHeight to do it. The web states the height instead, for the same reason: an
    ///input, a select and a button on one row must not disagree by a pixel.
    ///
    ///With one exception, and it is a deliberate one: the filled button is half a pixel shorter.
    ///Measured against its neighbours it was identical to the hundredth of a pixel and still read
    ///as the taller of the two, because a saturated fill against a white one looks larger than it
    ///is. The correction is optical, so it is the smallest number that does anything.
    ///
    test('every control on a row is 25px tall', async ({ page }) => {
        const heights = await page.evaluate(() =>
            [...document.querySelectorAll('.row > input, .row > select, .row > button, .row > a.btn')]
                .filter((el) => el.getBoundingClientRect().width > 0)
                .map((el) => ({
                    what: el.id || el.textContent.trim().slice(0, 18) || el.tagName,
                    primary: el.classList.contains('primary'),
                    h: Math.round(el.getBoundingClientRect().height * 10) / 10
                })));

        expect(heights.length).toBeGreaterThan(6);
        expect(heights.some((c) => c.primary)).toBe(true);
        for (const c of heights) {
            expect(c.h, `${c.what} is ${c.h}px`).toBe(c.primary ? 24.5 : 25);
        }
    });

    ///
    ///And 14px of text in it. A 30px box around 13px text was a lot of empty box around a small number.
    ///
    test('a control carries 14px text', async ({ page }) => {
        expect(await styleOf(page, '#addr', 'font-size')).toBe('14px');
        expect(await styleOf(page, '#timeout', 'font-size')).toBe('14px');
        expect(await styleOf(page, '#iface', 'font-size')).toBe('14px');
    });

    ///
    ///A label is one step under the control it names, not the same size as it.
    ///
    test('labels are 13px', async ({ page }) => {
        expect(await styleOf(page, 'label[for="addr"]', 'font-size')).toBe('13px');
    });

    ///
    ///
    ///A box on a caption line is not a control on a row either.
    ///
    ///There is nothing beside it held to the same height - a caption is text - so the full 25 made
    ///it the tallest thing on the line by a clear margin. Its label goes at the size of what is
    ///typed into it, so the words either side of the box and the words in it read as one line.
    ///
    test('a box on a caption line is shorter, and its label is the size of its text', async ({ page }) => {
        //The Command Library is the same card as a page, so this needs no instrument on the bench.
        await page.goto('/catalog');
        await expect(page.locator('.cap-row input[type=text]')).toBeVisible();

        const pair = await page.evaluate(() => {
            const line = document.querySelector('.cap-row');
            const box = line.querySelector('input[type=text]');
            const label = line.querySelector('label');
            return {
                height: Math.round(box.getBoundingClientRect().height * 10) / 10,
                boxFont: getComputedStyle(box).fontSize,
                labelFont: getComputedStyle(label).fontSize
            };
        });

        expect(pair.height).toBe(22);
        expect(pair.labelFont).toBe(pair.boxFont);
    });

    ///Buttons that are not row controls take their height from what they sit in. A menu row pinned to
    ///25 would be a menu row pretending to be a text box.
    ///
    test('menu rows are not held to the control height', async ({ page }) => {
        await page.locator('.titlebar .ghost').click();
        const items = page.locator('.settings .menu .item');
        await expect(items.first()).toBeVisible();

        const heights = await page.evaluate(() =>
            [...document.querySelectorAll('.settings .menu .item')]
                .filter((el) => el.getBoundingClientRect().height > 0)
                .map((el) => Math.round(el.getBoundingClientRect().height * 10) / 10));

        //All the same as each other - the thing that was actually wrong once, when the button rows took
        //the control font and the div beside them did not.
        expect(new Set(heights).size).toBe(1);
        expect(heights[0]).toBeGreaterThan(25);
    });
});

test.describe('glyph metrics', () => {
    ///
    ///Sixteen against twelve on the desktop, so eighteen against fourteen here.
    ///
    ///The drawn glyphs are outlines on the same 64-unit grid as AppIcons.Drawn, so they take the
    ///nominal size with no correction.
    ///
    test('a glyph with no correction is drawn at the nominal size', async ({ page }) => {
        //Export Results wears `saveFile`, which has no entry in the optical table - so it is the
        //nominal size itself, and what it measures is that the nominal is 18 rather than the 14 it was.
        const box = await boxOf(page, '.ico-savefile');
        expect(box.width).toBe(18);
        expect(box.height).toBe(18);
    });

    ///
    ///The bundled artwork is corrected optically, with ButtonStyle.Optical's own fractions.
    ///
    ///Solid shapes drawn edge to edge read as about twice the weight of an inset outline at the same
    ///nominal size, so the solid ones are drawn smaller. Matching the numbers is not the same as
    ///matching the eye - but the numbers are what can be asserted.
    ///
    test('the masked glyphs carry their optical factor', async ({ page }) => {
        //Scan wears `reset` at .78, Connect wears `connect` at .76.
        const scan = await boxOf(page, '.ico-scan');
        const connect = await boxOf(page, '.ico-connect');

        expect(scan.width).toBeCloseTo(18 * 0.78, 0);
        expect(connect.width).toBeCloseTo(18 * 0.76, 0);

        //And each is visibly smaller than nominal, which is the whole point of the table.
        expect(scan.width).toBeLessThan(18);
        expect(connect.width).toBeLessThan(18);
    });
});

test.describe('the vertical rhythm', () => {
    ///
    ///One gap above the first card and the same between each pair, so the stack reads as a stack.
    ///
    test('the cards are evenly spaced, and the first sits the same distance below the title bar', async ({ page }) => {
        const measured = await page.evaluate(() => {
            const cards = [...document.querySelectorAll('.workspace > .group')];
            const bar = document.querySelector('.titlebar').getBoundingClientRect();
            const r = (el) => el.getBoundingClientRect();
            const out = [Math.round((r(cards[0]).top - bar.bottom) * 10) / 10];
            for (let i = 1; i < cards.length; i++) {
                out.push(Math.round((r(cards[i]).top - r(cards[i - 1]).bottom) * 10) / 10);
            }
            return out;
        });

        expect(measured.length).toBeGreaterThan(2);
        for (const g of measured) expect(g).toBeCloseTo(8.8, 0);
    });

    ///
    ///A card's caption sits just under its top edge, and its first row just under the caption.
    ///
    test('a card gives its caption the same air above and below', async ({ page }) => {
        const card = await boxOf(page, '.workspace > .group');
        const cap = await boxOf(page, '.workspace > .group > .cap');
        const row = await boxOf(page, '.workspace > .group > .row');

        expect(cap.top - card.top).toBeCloseTo(7.4, 0);
        expect(row.top - cap.bottom).toBeCloseTo(9.6, 0);
    });

    ///
    ///And every caption starts at the same height, whatever else is on its line.
    ///
    ///Instrument Consoles shares its line with a button, and a button is nine pixels taller than a
    ///caption - so a centred row dropped those words six pixels lower than every other caption on the
    ///page. Asserted across all of them rather than on that one, because the thing that matters is that
    ///they agree.
    ///
    test('every card caption starts at the same height', async ({ page }) => {
        const tops = await page.evaluate(() =>
            [...document.querySelectorAll('.workspace > .group')].map((card) => {
                const cap = card.querySelector(':scope > .cap')
                         ?? card.querySelector(':scope > .cap-row > .cap');
                return Math.round((cap.getBoundingClientRect().top - card.getBoundingClientRect().top) * 10) / 10;
            }));

        expect(tops.length).toBeGreaterThan(2);
        expect(new Set(tops).size, `captions start at ${tops.join(', ')}`).toBe(1);
    });
});

test.describe('the title bar', () => {
    ///
    ///The gear ends where the cards end.
    ///
    ///Four pixels out is close enough to look like a mistake rather than a margin, which is what it was
    ///when the bar kept a smaller side padding than the workspace.
    ///
    test('the gear is flush with the right edge of the cards below it', async ({ page }) => {
        const gear = await boxOf(page, '.titlebar .ghost');
        const card = await boxOf(page, '.workspace > .group');

        expect(Math.abs(gear.right - card.right)).toBeLessThan(1.5);
    });

    ///
    ///And the bar does not grow to make room for it.
    ///
    test('the title bar stays 34px tall', async ({ page }) => {
        const bar = await boxOf(page, '.titlebar');
        expect(bar.height).toBe(34);
    });
});

//A card is a frame, and a frame is even.
//
test.describe('the padding of a card', () => {
    ///
    ///The bottom is the sides.
    ///
    ///Less air under a card's contents than beside them reads as a frame that ran out of room rather
    ///than one that was drawn, and it shows most on the console, where the command box sits at the
    ///foot of its card. SPEC 1 states the rule.
    ///
    test('gives its contents as much air below as beside', async ({ page }) => {
        //freshBench in beforeEach has already waited for the page.
        const pad = await page.evaluate(() => {
            const card = document.querySelector('main .group');
            const s = getComputedStyle(card);
            return { top: parseFloat(s.paddingTop), right: parseFloat(s.paddingRight),
                     bottom: parseFloat(s.paddingBottom), left: parseFloat(s.paddingLeft) };
        });

        expect(pad.left).toBe(pad.right);
        expect(pad.bottom).toBe(pad.left);

        //The top is the one that may be tighter, and only on a card that opens with a caption:
        //the caption brings its own air with it.
        expect(pad.top).toBeLessThanOrEqual(pad.bottom);
    });
});
