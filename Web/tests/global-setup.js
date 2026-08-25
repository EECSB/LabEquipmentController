//Build the server once, before any worker starts one.
//
//Each worker runs its own copy of the app (see e2e/fixtures.js for why), and `dotnet run` builds before
//it serves. Four of those starting at once race each other over obj/ and bin/ — MSBuild does not
//serialise across processes, and the failure is a half-written assembly rather than an error that says
//what happened.
//
//So the compile happens here, once, and the workers run `--no-build`. It also means the time the first
//navigation waits for is a server starting rather than a solution compiling.
const { spawn } = require('child_process');
const path = require('path');

const ROOT = path.join(__dirname, '..', '..');
const PROJECT = 'Web/LabEquipmentController.Web/LabEquipmentController.Web.csproj';

module.exports = async () => {
    await new Promise((resolve, reject) => {
        const build = spawn('dotnet', ['build', PROJECT, '--nologo', '-v', 'q'], {
            cwd: ROOT,
            stdio: ['ignore', 'pipe', 'pipe'],
            shell: process.platform === 'win32'
        });

        let noise = '';
        build.stdout.on('data', (d) => { noise += d; });
        build.stderr.on('data', (d) => { noise += d; });

        build.on('error', reject);
        build.on('exit', (code) => {
            if (code === 0) return resolve();
            //The build output is the only thing worth reading when this fails, and Playwright swallows
            //anything not thrown.
            reject(new Error(`dotnet build failed (${code}):\n${noise}`));
        });
    });
};
