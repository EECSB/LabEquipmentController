# The web version (Blazor, in a container)

The same bench in a browser. This folder holds two projects: a **Blazor WebAssembly
client** that is only a UI, and an **ASP.NET Core server** that owns every socket. A
browser cannot open a TCP connection to port 5025 and never will, so **all instrument
traffic happens on the server** — the client asks over HTTP, and script output streams back
over SignalR.

![The web build against the bench: two instruments discovered and connected, a console open on the generator](../docs/images/09-web.png)

## Run it

```bash
docker compose up --build     # then open http://localhost:8080
```

or, without Docker:

```bash
dotnet run --project Web/LabEquipmentController.Web
```

Both from the repository root — the Docker build context is the root, because the server
needs `Core/` (the transports and the catalogs) and the client project alongside it.

### Or pull the released image

```bash
docker run -d --network host -v lec-data:/data eecsb/labequipmentcontroller-web:latest
```

`latest` is the newest release; `1.2.3`, `1.2` and `1` pin as tightly as you like, and
`sha-abc1234` pins to a commit. **amd64 and arm64** — the second so a Raspberry Pi can sit on
the bench VLAN and be the thing that runs it.

Both architectures come out of one build. The publish is framework-dependent and
RID-agnostic, so what lands in the image is architecture-neutral IL; the one genuinely native
piece, `System.IO.Ports`' serial shim, is shipped by NuGet as every RID at once and resolved
when the process starts. Only the runtime base image differs, which is why
[Web/Dockerfile](Dockerfile) pins its *build* stage to `$BUILDPLATFORM` and leaves the
runtime stage unpinned.

[publish-docker.yml](../.github/workflows/publish-docker.yml) publishes on a `v*` tag, after
starting the image and checking it serves both halves. Its header has the one-time Docker Hub
setup, and why this one stores a token where the NuGet workflow stores nothing.

## What it can do

It reaches parity with the desktop app for everything that makes sense over a network:
scan and discovery, a console per instrument with that family's quick commands — detachable
into a browser tab of its own — the results table and plot, the command library with the
vendor's guide beside it, the script-language reference, single- and multi-instrument
script runners with live output, waveform and screen capture, the live meter readout, and
the two AI features.

[docs/UI-SPEC.md](../docs/UI-SPEC.md) holds the rule that keeps it that way: **the web
build is a control-for-control port of the desktop app, not a redesign of it** — the same
controls, in the same order, with the same words on them. Its §9 lists the handful of
places a browser forces a difference, and everything else that has no desktop counterpart
is a debt rather than a feature.

## Discovery needs the container on your network

Sweeping a subnet from inside Docker's default bridge network scans the container's own
private network and finds nothing, so the compose file uses `network_mode: host`. That mode
is **Linux-only** — on Docker Desktop for Windows or macOS the engine runs in a VM, so
"host" is the VM's network and not your laptop's, and discovery will not see the bench.
There, run the server directly with `dotnet run` instead, or give the container its own
address on the bench VLAN with macvlan.

## Serial ports belong to the server, and a container has none

Switch the scan card's heading to **Serial** and the whole card is about serial: it lists
the server's ports, `Scan` asks each of them for an identity at the baud rates you name, and
the Address box below becomes a list of those ports. They are the ports on **the machine
running the server**, not on the machine running the browser. A page in a browser cannot
open a serial port and never will, which is the same split every other instrument operation
here already has. Run the server on the desk with the instrument, then drive it from
anywhere.

Listing opens nothing, so arriving at the card is free. `Scan` opens ports — it writes
`*IDN?` to whatever is on the other end and asserts DTR and RTS on the way in, which resets
some boards — and it only ever opens the ports you chose. The button says so.

A container starts with no serial ports at all — host networking does not carry them, and
nothing else does either. To give it one, pass the device node through. There is a commented
block ready for it in [docker-compose.yml](../docker-compose.yml):

```yaml
devices:
  - "/dev/ttyUSB0:/dev/ttyUSB0"
```

or, running the image by hand:

```bash
docker run --device=/dev/ttyUSB0 …
```

Then the address is `serial:///dev/ttyUSB0` — the *container's* name for it, which is why the
mapping keeps both sides the same: two different names is one more thing to get wrong for no
gain. `ls /dev/ttyUSB* /dev/ttyACM*` on the host says which node the adapter came up as, and
`lec ports` inside the container says what actually arrived.

**Linux only**, for the same reason `network_mode: host` is: on Docker Desktop for Windows and
macOS the engine is in a VM that cannot see your USB adapter. Run the server directly there,
or drive that instrument with `lec` or the desktop app, both of which open a serial port
natively and need no container at all.

The **Serial** list is `GET /api/ports` and `GET /api/serial/list`, both of which list and
never open — the same `SerialPorts.Names()` the desktop's dropdown and `lec ports` use.
Running `lec ports` on the server, or inside the container, gives the same answer from a
shell. `POST /api/serial/scan` is the one that opens ports, and the page reaches it over the
hub so it can fill the list in as each port answers.

## Two things differ from the desktop app by necessity

**Connections belong to the server, not to a browser tab.** A sweep survives a refresh —
and two people with the page open are driving *one* bench, not two.

**The AI key comes from server configuration** (`Ai__ApiKey` — the compose file reads it
from a `.env` beside it), which means it is one key shared by everyone who can reach the
page; there is no Windows DPAPI in a Linux container to hold a per-user one.

Both are stated in the UI rather than left to be discovered.

## What the server writes outlives the container

AI connections set up in the page, catalogs a model extracted, and guides uploaded for the
library all land under `LEC_DATA` — `/data` in the image, kept on a named volume by the
compose file — so replacing the container does not throw them away.

## Tests

The client is covered end to end by Playwright, driving a real browser against a real
server:

```bash
cd Web/tests && npm install && npm run test:e2e
```

That run needs **no instrument**: a fake SCPI listener stands in for one, answering as an
SDM3065X over a loopback socket, which is all the app needs to classify it and open a
console for it.

It builds once and then starts a server per worker on ports 5111 upwards — one bench each,
because the bench is shared server state and two workers on one would clear each other's.
So **nothing else may be building while it runs**: a rebuild replaces the files under the
servers it is serving from, and the specs then fail against a half-written app rather than
anything real.

What those specs check is [docs/UI-SPEC.md](../docs/UI-SPEC.md): the control inventory, the
shared metrics and the enable rules. A finding is a diff against that document.

Nothing npm installs reaches `wwwroot` or the build — `dotnet build` stays the whole build.
The third-party libraries the page actually serves are vendored and committed; see the
[README beside them](LabEquipmentController.Web.Client/wwwroot/lib/README.md).
