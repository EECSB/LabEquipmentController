//One server per worker, so the suite can run in parallel.
//
//**Why it cannot simply share one.** The bench is a shared workspace on the server: sessions are
//sockets it holds, and `GET /api/sessions` answers the same list to every browser. Two workers against
//one server would see each other's consoles in the tab strip, and each one's `freshBench` would
//disconnect the other's instrument mid-test. That is why the suite ran single-file for as long as it
//did, and it is a fact about the application rather than about this machine.
//
//**So each worker gets its own.** A worker-scoped fixture starts a server on a port of its own, hands
//its address down as `baseURL`, and stops it when the worker ends. Playwright's own `webServer` cannot
//do this — it is one server for the whole run — so this replaces it.
//
//The compile happens once in global-setup.js and every server here runs `--no-build`: four `dotnet run`
//processes building the same project at the same time race over obj/ and bin/, and MSBuild does not
//serialise across processes.
const base = require('@playwright/test');
const { spawn } = require('child_process');
const path = require('path');

const ROOT = path.join(__dirname, '..', '..', '..');

///
///The built server, run directly rather than through `dotnet run`.
///
///`dotnet run` is a launcher: it builds, then starts the app as a *child* of itself. Killing the
///launcher leaves the app holding its port and its build output — which is exactly what happened, four
///times over, and the symptom was the preview server refusing to start afterwards because something
///invisible had bin/ locked. Running the assembly is one process, and killing it kills the server.
///
///It also skips the build, which global-setup.js has already done once for all of them.
///
const PROJECT_DIR = path.join(ROOT, 'Web', 'LabEquipmentController.Web');
const SERVER_DLL = path.join(PROJECT_DIR, 'bin', 'Debug', 'net10.0', 'LabEquipmentController.Web.dll');

///Where worker 0 listens. The rest count up from here, and 5099 is left alone — that is where the
///preview runs, and a suite that took it over would fight whatever is being looked at.
const FIRST_PORT = 5111;

///How long to wait for one to come up. Generous: four are starting at once on a machine that is also
///running four browsers.
const START_MS = 120000;

async function reachable(url) {
    try {
        const res = await fetch(url, { signal: AbortSignal.timeout(2000) });
        return res.status < 500;
    } catch {
        return false;
    }
}

///Start one server and resolve with its address once it answers.
async function startServer(port, dataDir) {
    const child = spawn(
        'dotnet',
        [SERVER_DLL, '--urls', `http://localhost:${port}`],
        {
            //The project directory, because the host takes its content root from the working
            //directory — which is what `dotnet run` gave it, and what the static web assets are
            //resolved against.
            cwd: PROJECT_DIR,
            stdio: ['ignore', 'pipe', 'pipe'],
            //No shell. On Windows a shell puts cmd.exe between here and the server, and killing a
            //shell does not kill what it started.
            env: {
                ...process.env,
                //Development, stated rather than inherited: without a launch profile the host comes up
                //in Production, and in Production CreateBuilder does not call UseStaticWebAssets — so
                //nothing serves index.html and every request 404s while the log says "Now listening".
                ASPNETCORE_ENVIRONMENT: 'Development',
                //Its own data directory, so one worker's AI settings are not another's — and so a run
                //cannot read or write what a real session saved.
                LEC_DATA: dataDir,
                Ai__ApiKey: ''
            }
        });

    //Drained rather than inherited: a pipe nobody reads fills up and stops the process it belongs to.
    let log = '';
    const keep = (d) => { log = (log + d).slice(-4000); };
    child.stdout.on('data', keep);
    child.stderr.on('data', keep);

    let died = null;
    child.on('exit', (code) => { died = code; });

    const url = `http://localhost:${port}`;
    const until = Date.now() + START_MS;
    while (Date.now() < until) {
        if (died !== null) throw new Error(`server on ${port} exited (${died}):\n${log}`);
        if (await reachable(url)) return { url, child };
        await new Promise((r) => setTimeout(r, 300));
    }

    child.kill();
    throw new Error(`server on ${port} did not answer within ${START_MS}ms:\n${log}`);
}

const test = base.test.extend({
    ///The worker's own server. Started once per worker, whatever it goes on to run.
    server: [
        async ({}, use, workerInfo) => {
            const port = FIRST_PORT + workerInfo.workerIndex;
            const dataDir = path.join(__dirname, '..', '.data', String(workerInfo.workerIndex));
            const { url, child } = await startServer(port, dataDir);

            await use(url);

            //One process, so one kill. Waited for, because a worker that finished before its server
            //had let go of the port would hand the next run a port that is busy for a second or two.
            child.kill();
            await new Promise((resolve) => {
                if (child.exitCode !== null || child.signalCode !== null) return resolve();
                child.once('exit', resolve);
                setTimeout(resolve, 5000);
            });
        },
        { scope: 'worker' }
    ],

    ///Point every navigation at this worker's server rather than at the one in the config.
    baseURL: async ({ server }, use) => {
        await use(server);
    }
});

module.exports = { test, expect: base.expect };
