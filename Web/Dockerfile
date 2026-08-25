# Build and run the web version.
#
#   docker build -f Web/Dockerfile -t labequipmentcontroller-web .
#
# The build context is the repository root, because the server needs Core (the transports
# and the catalogs) and the client project alongside it — a context of Web/ alone could not
# see them.
#
# ─── Why the build stage pins itself to the build machine's architecture ────────────────
# $BUILDPLATFORM is the machine doing the building; the runtime stage below has no such
# pin, so buildx pulls the *target* architecture's aspnet image for it. The result is one
# amd64 build producing both an amd64 and an arm64 image — no QEMU for the expensive part.
#
# That is only sound because the publish is framework-dependent and RID-agnostic, so what
# lands in /app is architecture-neutral IL. The one thing in this app that is genuinely
# native — System.IO.Ports' serial shim — is shipped by NuGet as every RID at once
# (runtimes/linux-arm64/native/libSystem.IO.Ports.Native.so and eleven siblings), and the
# host picks the right one when the process starts. Add a `-r` to that publish and this
# stops being true.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The wasm-tools workload relinks the WebAssembly runtime natively on publish, and that
# step drives emscripten, which is a Python program. The SDK image has no Python, so
# without this the publish fails four times over with "unable to find python in $PATH" —
# an error that names neither Blazor nor the workload that asked for it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && rm -rf /var/lib/apt/lists/*

# Restore against project files only, so editing a .cs file does not invalidate this layer.
COPY Core/LabEquipmentController.Core.csproj Core/
COPY Web/LabEquipmentController.Web/LabEquipmentController.Web.csproj Web/LabEquipmentController.Web/
COPY Web/LabEquipmentController.Web.Client/LabEquipmentController.Web.Client.csproj Web/LabEquipmentController.Web.Client/
RUN dotnet workload install wasm-tools --skip-manifest-update \
 && dotnet restore Web/LabEquipmentController.Web/LabEquipmentController.Web.csproj

COPY Core/ Core/
COPY Web/ Web/
RUN dotnet publish Web/LabEquipmentController.Web/LabEquipmentController.Web.csproj \
        -c Release -o /app --no-restore

# The runtime image carries no SDK: the catalogs are embedded in the Core assembly, so the
# published output is genuinely all that is needed.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Extracted catalogs are written here; the compose file mounts a volume over it so they
# survive the container being replaced.
ENV LEC_DATA=/data
RUN mkdir -p /data

# Host networking ignores this, but it is the right default when it is not used.
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "LabEquipmentController.Web.dll"]
