//
//The bench page: the two cards above the consoles, and what is on them.
//
//Everything here is checkable without an instrument, which is the point - these are the controls a
//person meets before there is anything to connect to, and the ones that went missing in the port
//(UI-SPEC §3). The headings, the empty table, the timeout box and the export button were each found by
//eye and reported one at a time; each is one assertion here.
//
const { test, expect } = require('./fixtures');
const { freshBench, boxOf, rowGaps } = require('./helpers');

test.beforeEach(async ({ page, request }) => {
    await freshBench(page, request);
});

test.describe('the title bar', () => {
    ///
    ///The bench is the app, not one of its windows, so it adds nothing to the app's name - which is
    ///all `MainForm`'s title bar carries. It used to read "— SCPI over Ethernet", a strapline the
    ///desktop build has nowhere (UI-SPEC §2).
    ///
    test('carries the app name alone on the bench', async ({ page }) => {
        await expect(page.locator('.titlebar .name')).toHaveText('Lab Equipment Controller');
        await expect(page.locator('.titlebar .sub')).toHaveCount(0);
        await expect(page).toHaveTitle('Lab Equipment Controller');
    });

    ///
    ///A page that is one of the windows names itself, the way that window's own title bar does.
    ///
    test('names the window on a page that is one', async ({ page }) => {
        await page.goto('/catalog');
        await expect(page.locator('.titlebar .sub')).toHaveText('— Command Library');
        await expect(page).toHaveTitle('Command Library — Lab Equipment Controller');
    });
});

test.describe('network scan card', () => {
    ///
    ///Three labelled controls and the button that answers them, in that order.
    ///
    test('carries interface, range and ports, then Scan', async ({ page }) => {
        //The heading is a control now, so it is checked as one. The selected half *is* the card's
        //title - asserting the text alone would pass just as well on a card that had lost the
        //switch and gone back to a word.
        const cap = page.locator('.workspace > .group').first().locator('.cap');
        await expect(cap.locator('.seg button.on')).toHaveText('Network Scan');
        await expect(cap.locator('.seg button')).toHaveCount(2);

        //And `Scan` below is exact, because "Network Scan" would answer to a substring match.

        await expect(page.getByText('Interface:', { exact: true })).toBeVisible();
        await expect(page.getByText('IP range:', { exact: true })).toBeVisible();
        await expect(page.getByText('SCPI Port(s):', { exact: true })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Scan', exact: true })).toBeEnabled();
    });

    ///
    ///The ports box is filled, not hinted.
    ///
    ///An empty box swept all four of NetworkScanner.CommonScpiPorts while its placeholder claimed two,
    ///so the hint was not merely unfilled but wrong. UI-SPEC §3.2.
    ///
    test('the ports box arrives holding the four common ports', async ({ page }) => {
        await expect(page.locator('#ports')).toHaveValue('5025, 5555, 3490, 111');
    });

    ///
    ///The bar and its status line are on the page with nothing running.
    ///
    ///A row that appeared when Scan was pressed would shove the list below it down at the moment you
    ///had started watching it, and shove it back when the scan ended.
    ///
    test('the progress row is present and says Ready before anything runs', async ({ page }) => {
        await expect(page.locator('.bar')).toBeVisible();
        await expect(page.locator('.bar + .muted, .bar ~ .muted').first()).toHaveText('Ready.');
    });

    ///
    ///The heading is the control. Each half is a whole caption, not a word with a shared "Scan"
    ///after it: the two read as the two things this card can be, and the selected one is its title.
    ///
    ///The desktop draws the same switch over its group box's top border, and it governs the whole
    ///card: these inputs, the columns of the list below, and which box the Address row shows. It
    ///used to sit on the address row, which left this card saying "Network Scan" while the box
    ///under it held COM3 - UI-SPEC §3.2.
    ///
    test('the heading carries the Network / Serial switch', async ({ page }) => {
        const seg = page.locator('.group .cap .seg button');
        await expect(seg).toHaveText(['Network Scan', 'Serial Scan']);

        //Network is on, and it is on because nothing has been chosen yet.
        await expect(seg.first()).toHaveClass(/\bon\b/);
        await expect(seg.last()).not.toHaveClass(/\bon\b/);

        //Each half as wide as its own caption. Uneven on purpose: matched to the wider of the two,
        //the switch carried a block of empty space beside the shorter one.
        const widths = await seg.evaluateAll((bs) => bs.map((b) => b.getBoundingClientRect().width));
        expect(widths[0]).toBeGreaterThan(widths[1]);

        //And it is the size of the caption it stands in, not of the boxes below it. Sized to a
        //form control it stood half again as tall as the words beside it.
        const box = await boxOf(page, '.group .cap .seg');
        const field = await boxOf(page, '#ports');
        expect(box.height).toBeLessThan(field.height);
    });

    ///
    ///The chosen half is filled grey, as SegmentButton fills it on the desktop, and not in the
    ///accent: nothing else on the card is accent coloured, and an accent-filled heading was the one
    ///thing on screen pulling the eye. Both captions keep the heading's ink - which one is chosen is
    ///the fill's to say - and hovering the chosen half leaves it as it is.
    ///
    test('fills the chosen half grey, as the desktop does', async ({ page }) => {
        const seg = page.locator('.group .cap .seg button');
        const paint = (b) => b.evaluate((el) => {
            const s = getComputedStyle(el);
            return { fill: s.backgroundColor, ink: s.color };
        });
        const theme = await page.evaluate(() => {
            const s = getComputedStyle(document.documentElement);
            return { seg: s.getPropertyValue('--seg-on').trim(), accent: s.getPropertyValue('--accent').trim() };
        });
        expect(theme.seg).not.toBe(theme.accent);

        const chosen = await paint(seg.first());
        const other = await paint(seg.last());
        const [r, g, b] = chosen.fill.match(/\d+/g).map(Number);
        expect(Math.max(r, g, b) - Math.min(r, g, b)).toBeLessThan(24);   // a grey, give or take a tint
        expect(other.fill).toBe('rgba(0, 0, 0, 0)');
        expect(other.ink).toBe(chosen.ink);

        await seg.first().hover();
        expect((await paint(seg.first())).fill).toBe(chosen.fill);
    });
});

test.describe('serial scan card', () => {
    ///
    ///Switching the heading switches the whole card: the two inputs, and the words naming them.
    ///
    ///"Ports:" is the counterpart of Interface - how much of the bench this covers - and the baud
    ///list is the counterpart of SCPI Port(s), a short written list of the values worth trying.
    ///
    test('Serial swaps the scan inputs for ports and baud rates', async ({ page }) => {
        await page.locator('.group .cap .seg button', { hasText: 'Serial Scan' }).click();

        await expect(page.getByText('Ports:', { exact: true })).toBeVisible();
        await expect(page.getByText('Baud rate(s):', { exact: true })).toBeVisible();

        //And the network inputs are gone rather than disabled: they ask about a bench that is
        //not the one being looked at.
        await expect(page.locator('#iface')).toHaveCount(0);
        await expect(page.locator('#range')).toHaveCount(0);
        await expect(page.locator('#ports')).toHaveCount(0);

        //One Scan either way. Which sweep it starts is the switch above, not a second control.
        await expect(page.getByRole('button', { name: 'Scan', exact: true })).toHaveCount(1);
    });

    ///
    ///Filled, not hinted - the same rule the SCPI port box follows, and for the same reason: an
    ///empty box would still sweep the defaults, and a hint that does not name them is wrong.
    ///
    test('the baud box arrives holding the rates the scan tries', async ({ page }) => {
        await page.locator('.group .cap .seg button', { hasText: 'Serial Scan' }).click();
        await expect(page.locator('#bauds')).toHaveValue('9600, 115200, 19200, 38400, 57600');
    });

    ///
    ///The list is named for what it holds. A serial port has no port number; the second column
    ///carries the line settings instead, which is the thing that has to match before a character
    ///gets through.
    ///
    test('the columns are renamed for a serial bench', async ({ page }) => {
        await page.locator('.group .cap .seg button', { hasText: 'Serial Scan' }).click();

        const heads = page.locator('.scroll.fit thead th');
        await expect(heads.nth(0)).toHaveText('Port');
        await expect(heads.nth(1)).toHaveText('Settings');
        await expect(heads.nth(2)).toHaveText('Protocol');
        await expect(heads.nth(3)).toContainText('Identity (*IDN?)');
    });

    ///
    ///The test server has no serial ports, so the card says so twice: once in the status line
    ///where a scan reports, and once as the note that says how to give a container one.
    ///
    ///Worth asserting because it is the state every reader meets first. A card that simply drew
    ///nothing would read as a card that failed to load.
    ///
    test('a server with no ports says so, and says what to do about it', async ({ page }) => {
        await page.locator('.group .cap .seg button', { hasText: 'Serial Scan' }).click();

        await expect(page.locator('.bar ~ .muted').first()).toHaveText('No serial ports on the server.');
        await expect(page.locator('.group').first().locator('.error')).toContainText('devices:');
    });

    ///
    ///Switching back leaves no trace of the other bench.
    ///
    ///Rows from one mode are not merely stale in the other, they are unreadable: an IP address
    ///under a column headed "Port". And a row that fills the Address box has to fill it with
    ///something the visible box can hold.
    ///
    test('switching back restores the network card', async ({ page }) => {
        const seg = page.locator('.group .cap .seg button');

        await seg.filter({ hasText: 'Serial Scan' }).click();
        await expect(page.locator('#bauds')).toBeVisible();

        await seg.filter({ hasText: 'Network Scan' }).click();
        await expect(page.locator('#iface')).toBeVisible();
        await expect(page.locator('#bauds')).toHaveCount(0);
        await expect(page.locator('.scroll.fit thead th').nth(0)).toHaveText('IP Address');
        await expect(page.locator('.bar ~ .muted').first()).toHaveText('Ready.');
    });

});

test.describe('discovered instruments', () => {
    ///
    ///The desktop's four headings, letter for letter.
    ///
    ///`Identity (*IDN?)`, not `Identity`. These are also the header row ScanResultExport writes into
    ///the CSV, so the screen and the file name the same four things the same way.
    ///
    test('the four column headings match the desktop exactly', async ({ page }) => {
        const heads = page.locator('.scroll.fit thead th');
        await expect(heads).toHaveCount(4);

        //The first three are the whole cell. The fourth carries Export Results as well as its name,
        //so it is the name that is checked rather than everything in the cell.
        await expect(heads.nth(0)).toHaveText('IP Address');
        await expect(heads.nth(1)).toHaveText('Port');
        await expect(heads.nth(2)).toHaveText('Protocol');
        await expect(heads.nth(3)).toContainText('Identity (*IDN?)');
    });

    ///
    ///And no fifth column: the per-row Connect button and the column holding it are gone. Click fills
    ///the address box, double-click connects, which is the gesture the caption names.
    ///
    test('there is no per-row connect column', async ({ page }) => {
        await expect(page.locator('.scroll.fit tbody button')).toHaveCount(0);
        await expect(page.locator('.group', { hasText: 'Discovered Instruments' }).first())
            .toContainText('(double-click a row to connect)');
    });

    ///
    ///The table stands with nothing in it, keeping one row.
    ///
    ///A list view keeps its row area whatever is in it. Four headings alone read as a table that failed
    ///to draw rather than one waiting to be filled - UI-SPEC §3.3.
    ///
    test('an empty table keeps one blank row', async ({ page }) => {
        const rows = page.locator('.scroll.fit tbody tr');
        await expect(rows).toHaveCount(1);
        await expect(rows.first()).toHaveClass(/blank/);
        await expect(rows.first()).toHaveText('');

        //And it is a row's worth of height, not a hairline: within a few pixels of the header above it,
        //which runs a little taller for carrying a button.
        const blank = await boxOf(page, '.scroll.fit tbody tr');
        const head = await boxOf(page, '.scroll.fit thead tr');
        expect(Math.abs(blank.height - head.height)).toBeLessThan(6);
    });

    ///
    ///Export Results sits in the last heading, hard against the table's right edge.
    ///
    ///Place(btnExport, lstDevices.Right - btnExport.Width) is how the desktop says it. In the heading
    ///rather than under the table, because a row of its own was one button and a card's width of
    ///nothing; and in the *last* heading, so it follows a wide identity string out to the new edge.
    ///
    test('Export Results is in the last heading, at the table right edge', async ({ page }) => {
        const button = page.locator('.scroll.fit thead').getByRole('button', { name: /Export Results/ });
        await expect(button).toHaveCount(1);

        const table = await boxOf(page, '.scroll.fit table');
        const box = await boxOf(page, '.scroll.fit thead button');
        expect(table.right - box.right).toBeLessThan(12);

        //And well clear of the card's own edge, which is what it used to be pinned to.
        const card = await boxOf(page, '.group:has(.scroll.fit)');
        expect(card.right - box.right).toBeGreaterThan(20);
    });

    ///
    ///The list is never narrower than the row of controls under it.
    ///
    ///Empty, it stopped in the middle of the card: four headings and a frame ending halfway
    ///across, which reads as a table that failed to draw rather than one waiting to be filled.
    ///Its right edge is Connect's. Full of long identity strings it is the wider of the two and
    ///the row stays where it is.
    ///
    test('reaches the end of the row under it', async ({ page }) => {
        const box = await boxOf(page, '.fitcol > .scroll.fit');
        //The row's own last button. Not a descendant one: the address-kind switch holds two
        //buttons of its own, and the first of those comes long before Connect.
        const connect = await boxOf(page, '.fitcol > .row > button:last-of-type');

        expect(Math.abs(box.right - connect.right)).toBeLessThan(1.5);
    });

    ///
    ///It does not push the heading row taller than a data row.
    ///
    ///A button dropped into a cell brings a control's height with it. Trimmed the way one in a data row
    ///is: it belongs to the table rather than standing on its own.
    ///
    test('the heading row stays a row', async ({ page }) => {
        const head = await boxOf(page, '.scroll.fit thead tr');
        const body = await boxOf(page, '.scroll.fit tbody tr');

        expect(head.height - body.height).toBeLessThan(8);
    });

    ///
    ///Nothing to export until there is a list to write.
    ///
    test('Export Results is dead while the list is empty', async ({ page }) => {
        await expect(page.getByRole('button', { name: /Export Results/ })).toBeDisabled();
    });
});

test.describe('the address row', () => {
    ///
    ///The desktop's numbers exactly: 100 to 30000, in steps of 500, starting at 3000.
    ///
    test('the timeout box is the desktop box', async ({ page }) => {
        const timeout = page.locator('#timeout');
        await expect(timeout).toHaveValue('3000');
        await expect(timeout).toHaveAttribute('min', '100');
        await expect(timeout).toHaveAttribute('max', '30000');
        await expect(timeout).toHaveAttribute('step', '500');
    });

    ///
    ///Three distances and no fourth: two to a box from the label naming it, the row's own step between
    ///one pair and the next, and fifty-two before the control that answers the row.
    ///
    ///Asserted as a sequence rather than one pair at a time, so a control going missing - which merges
    ///two gaps into one that may happen to measure right - fails here.
    ///
    ///The row holds no switch of its own: which box shows is the scan card's heading, one card up.
    ///
    test('carries the three distances of the spec, in order', async ({ page }) => {
        const gaps = await rowGaps(page, '.group:has(.scroll.fit) .row:has(#addr)');

        //Address: | address box | Timeout (ms): | box | spacer | Connect
        expect(gaps.map((g) => g.px)).toEqual([2, 9.6, 2, 9.6, 9.6]);

        //The last two are the answer gap: 9.6 to the spacer, the spacer, 9.6 to Connect. Fifty-two
        //across, which is AnswerGapLogical.
        const box = await boxOf(page, '#timeout');
        const connect = await boxOf(page, '.row:has(#addr) > button');
        expect(Math.round(connect.left - box.right)).toBe(52);
    });

    ///
    ///The row carries no switch of its own.
    ///
    ///One choice, one control. The card, its columns and this box are all about the same bench, and
    ///two switches that had to agree would eventually not - which is exactly what the pair did while
    ///the switch was here: the card above went on saying "Network Scan" with COM3 in the box.
    ///
    test('the address row holds no kind switch', async ({ page }) => {
        await expect(page.locator('.row:has(#addr) .seg')).toHaveCount(0);
    });

    ///
    ///Serial swaps the box in place - the row is "Address:", singular - and the list it grows is the
    ///server's ports, since a browser has none of its own. Driven from the heading one card up.
    ///
    test('Serial swaps the address box for the port list, at the same width', async ({ page }) => {
        const before = await boxOf(page, '#addr');
        expect(await page.locator('#addr').getAttribute('list')).toBeNull();

        await page.locator('.group .cap .seg button', { hasText: 'Serial Scan' }).click();

        expect(await page.locator('#addr').getAttribute('list')).toBe('serialports');
        expect(await page.locator('#addr').count()).toBe(1);

        const after = await boxOf(page, '#addr');
        expect(Math.round(after.width)).toBe(Math.round(before.width));
        expect(Math.round(after.left)).toBe(Math.round(before.left));
    });

    ///
    ///The address box is sized for an address, not for a paragraph.
    ///
    test('the address box is one endpoint wide', async ({ page }) => {
        const addr = await boxOf(page, '#addr');
        const card = await boxOf(page, '.group:has(.scroll.fit)');

        expect(addr.width).toBeGreaterThan(120);
        expect(addr.width).toBeLessThan(280);
        expect(addr.width).toBeLessThan(card.width / 3);
    });
});

test.describe('instrument consoles', () => {
    ///
    ///The multi-instrument button is here whether or not anything is connected: such a script is
    ///written before the instruments are connected as often as after.
    ///
    test('Multi-Instrument Scripts is reachable with an empty bench', async ({ page }) => {
        await expect(page.getByRole('button', { name: /Multi-Instrument Scripts/ })).toBeEnabled();
    });

    ///
    ///And a sentence stands where the tab strip would be, because a blank panel with no explanation
    ///reads as a broken window.
    ///
    test('an empty bench says so where the tabs would be', async ({ page }) => {
        await expect(page.locator('.group.consoles .none')).toContainText('No instrument connected.');
        await expect(page.locator('.tabstrip')).toHaveCount(0);
    });
});
