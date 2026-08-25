//Playwright configuration for the web client's end-to-end tests.
//
//There is no `webServer` here, deliberately: that starts one server for the whole run, and one server
//is exactly what this suite cannot share. The bench is a shared workspace — sessions live on the server
//and `GET /api/sessions` answers the same list to every browser — so two workers against one server
//would see each other's consoles and clear each other's benches. Each worker starts its own instead;
//see e2e/fixtures.js. The compile happens once, in global-setup.js, before any of them.
const path = require('path');
const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
    testDir: './e2e',
    timeout: 60000,
    expect: { timeout: 10000 },

    globalSetup: require.resolve('./global-setup'),

    //
    //Four servers, four browsers, and tests spread across all of them.
    //
    //It ran single-file until each worker could have a bench of its own, which is a fact about the
    //application rather than about this machine: one server, one bench. With a server per worker there
    //is nothing left to share, so the only ceiling is cores — and each worker is a browser booting its
    //own copy of the WASM runtime, plus a dotnet process holding sockets.
    //
    //Four rather than more because a few of these specs wait on a fake instrument answering on a timer,
    //and a machine saturated enough to delay those timers makes them fail for being slow rather than
    //for being wrong. Re-measure before changing it, and take two full runs before believing a number.
    //
    fullyParallel: true,
    workers: process.env.CI ? 2 : 4,

    //One retry, so a spec that fails for a reason nothing here controls is retried rather than believed
    //immediately. A spec that fails twice has something to say.
    //
    //Locally too, since the tool windows grew: four browsers, four servers and four instrument
    //simulators on one machine, and the specs that drive a window with the mouse are the ones that
    //feel it — a press dispatched before the window it lands on has been wired does nothing at all.
    //The specs wait for the wiring now (see settle in helpers.js); this is for the rest of what a
    //saturated machine does to a timed gesture.
    retries: 1,

    reporter: [['list']],

    use: {
        //Overridden per worker by the `server` fixture — this is only what a spec would get if it ran
        //without one, and pointing it at a port nothing serves is better than pointing it at 5099,
        //where the preview lives.
        baseURL: 'http://localhost:5111',

        //Wide enough that the bench does not fold onto one column: .split drops to a single column
        //below 1100px, and the specs about the log and results panels standing side by side need them
        //beside each other. Tall enough that the console is not the only thing on screen.
        viewport: { width: 1400, height: 1000 },
        trace: 'on-first-retry'
    },

    projects: [
        { name: 'chromium', use: { ...devices['Desktop Chrome'] } }
    ],

    //Where each worker's server writes. Kept here rather than in the fixture so the .gitignore entry
    //and the path have one home.
    metadata: { dataRoot: path.join(__dirname, '.data') }
});
