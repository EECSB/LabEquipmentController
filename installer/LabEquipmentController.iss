; Inno Setup script for Lab Equipment Controller.
;
; The payload is the FRAMEWORK-DEPENDENT build — one 12 MB executable against the .NET 10
; Desktop Runtime, rather than the 53 MB self-contained one the portable zip carries. That
; is the whole point of the installer: a small download for machines that have the runtime
; (or are willing to let this fetch it), while the zip stays the answer for a machine with
; no .NET at all and no wish to install any.
;
; Build the payload first:
;   dotnet publish LabEquipmentController.csproj -c Release -r win-x64 ^
;       --self-contained false -p:PublishSingleFile=true ^
;       -p:PublishDir=bin\Release\publish\win-x64-fd\
;
; Then compile this script:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\LabEquipmentController.iss
; (a machine-wide install puts ISCC.exe under "C:\Program Files (x86)\Inno Setup 6\" instead)
;
; Verify the result with installer\Test-Installer.ps1 — no elevation needed, because this
; installs per-user by default.
;
; Output: bin\LabEquipmentController-v<version>-setup.exe

#define AppName      "Lab Equipment Controller"
#define AppVersion   "1.0.0"
#define AppPublisher "The EECS Blog"
#define AppURL       "https://github.com/EECSB/LabEquipmentController"
#define AppExe       "LabEquipmentController.exe"
#define BuildDir     "..\bin\Release\publish\win-x64-fd"

; Microsoft's own permalink for the latest 10.0 patch, resolving to
; builds.dotnet.microsoft.com. Only ever offered, never installed behind the user's back.
#define RuntimeUrl   "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define RuntimePage  "https://dotnet.microsoft.com/download/dotnet/10.0/runtime"

[Setup]
AppId={{03306498-E51D-4091-AB94-CA8850D0E90C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\LabEquipmentController
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\app.ico

OutputDir=..\bin
OutputBaseFilename=LabEquipmentController-v{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; The app talks to instruments over TCP and writes its settings under %AppData% — nothing
; here needs administrator rights. So install per-user by default, which asks for no UAC
; prompt at all, and let anyone who wants a machine-wide install say so in the dialog (or
; pass /ALLUSERS on the command line).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The single-file publish leaves one native library beside the executable: WebView2Loader,
; which the Command Library's guide viewer needs. The app survives its absence — it checks
; whether WebView2 is usable and hides the viewer if not — but shipping it is what makes
; the feature work at all.
Source: "{#BuildDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;

{ A framework-dependent build cannot start without its runtime, so the one failure this
  installer must not allow is a successful install that produces an app which dies on
  launch. A .NET build does not roll forward across major versions by itself, so the 9.x
  runtime a previous release of this app may have installed does not count: look for a
  10.x directory specifically. }
function DesktopRuntime10Present(): Boolean;
var
  Root: String;
  FR: TFindRec;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if FindFirst(Root + '\10.*', FR) then
  begin
    try
      repeat
        if (FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
                                     SetupMessage(msgPreparingDesc), nil);
end;

function InstallRuntime(): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      { A failed download is not a failed install — the app is still worth putting on disk,
        and the runtime can be fetched by hand. Say where from. }
      SuppressibleMsgBox('The .NET runtime could not be downloaded:' + #13#10#13#10
        + AddPeriod(GetExceptionMessage) + #13#10#13#10
        + 'Install it yourself from' + #13#10 + '{#RuntimePage}' + #13#10#13#10
        + 'Setup will continue.', mbError, MB_OK, IDOK);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  { /passive rather than /quiet: the runtime installer elevates itself, and a UAC prompt
    with no visible window behind it is what makes an installer look like malware. }
  if not ShellExec('', ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'),
                   '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated,
                   ResultCode) then
    Exit;

  { 0 = installed, 3010 = installed, wants a restart, 1638 = a newer build is already here. }
  Result := (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1638);
  if not Result then
    SuppressibleMsgBox('The .NET runtime installer returned code ' + IntToStr(ResultCode)
      + '.' + #13#10#13#10 + 'Setup will continue, but ' + '{#AppName}'
      + ' will not start until the runtime is installed from' + #13#10
      + '{#RuntimePage}', mbError, MB_OK, IDOK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;
  if DesktopRuntime10Present() then
    Exit;
  { A silent install is somebody's script, and a script did not ask to be handed a 58 MB
    download or a UAC prompt. Leave the prerequisite to whoever wrote it. }
  if WizardSilent then
    Exit;

  case SuppressibleMsgBox('{#AppName} needs the .NET 10 Desktop Runtime, which is not'
    + ' installed on this PC.' + #13#10#13#10
    + 'Download and install it now? (about 57 MB, from Microsoft)' + #13#10#13#10
    + 'Choose No to install ' + '{#AppName}' + ' anyway — it will not start until the'
    + ' runtime is there. The portable download on the releases page needs no runtime'
    + ' at all.', mbConfirmation, MB_YESNOCANCEL, IDYES) of
    IDYES:  InstallRuntime();
    IDNO:   ;
    IDCANCEL: Result := False;
  end;
end;
