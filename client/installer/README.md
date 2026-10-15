# MeetVault installer

A **per-user Windows installer**: the user downloads one file, double-clicks it, and the app is
installed with everything it needs. No administrator rights, no UAC prompt, no certificate
ceremony, no separate model staging.

## Build it

```powershell
cd client
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

That script:

1. stages the payload through the **one source of truth**, `tools\pack-portable.ps1 -SkipZip`
   (clean publish + the two shipped Vosk models + `langid` + `vcruntime140*.dll`);
2. refuses to continue unless `MeetVault.exe` **and both shipped models** are present;
3. compiles `installer\MeetVault.iss` with Inno Setup 6 (`ISCC.exe`) and prints the setup's size
   and SHA256.

Requirements: .NET 8 SDK, the staged `models/` tree, and Inno Setup 6 — installed per-user, no admin:

```powershell
winget install --id JRSoftware.InnoSetup --scope user --silent
# or: innosetup-6.7.3.exe /VERYSILENT /CURRENTUSER
```

Output: `client\publish\MeetVaultSetup-1.0.0.exe`.

## Install it (what the user does)

- Double-click `MeetVaultSetup-1.0.0.exe` → Next → Install → optional "Launch MeetVault".
- Silent: `MeetVaultSetup-1.0.0.exe /VERYSILENT /CURRENTUSER`.
- It lands in `%LOCALAPPDATA%\Programs\MeetVault` (per-user, `PrivilegesRequired=lowest`), with a
  Start-menu entry, an optional desktop icon, and an uninstaller in *Apps & features*.
- An admin who wants a machine-wide install can run `MeetVaultSetup-1.0.0.exe /ALLUSERS`
  (`PrivilegesRequiredOverridesAllowed=commandline`).

**Uninstall keeps the user's data**: recordings, `meetings.db` and the sign-in token live under
`%APPDATA%\MeetVault` and are deliberately untouched.

## What needs elevation (the only elevated steps)

1. **Nothing, for the normal per-user install.** Build and install both run unelevated.
2. **A machine-wide install**: `MeetVaultSetup-1.0.0.exe /ALLUSERS` from an elevated prompt.
3. **To remove the SmartScreen warning**: sign the binaries with a code-signing certificate.
   Order matters — sign the staged exe *before* building the installer, then sign the installer:

   ```powershell
   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f <cert>.pfx `
       publish\win-x64\MeetVault.exe
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-installer.ps1 -SkipStage
   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f <cert>.pfx `
       publish\MeetVaultSetup-1.0.0.exe
   ```

   Unsigned, a regular user sees "Windows protected your PC" and clicks *More info → Run anyway* —
   it installs fine, one extra click.
4. **The MSIX route** (if that is ever wanted instead): trusting the signing certificate needs admin.
   MSIX is only worth it with a real code-signing certificate — a self-signed one is not a
   regular-user experience.

## Why the app has a DPAPI fallback (and why it is NOT the reason for this installer)

While setting this up I read `CredentialLockerSecretStore` and concluded that the WinRT
`PasswordVault` needs package identity and silently no-ops without it — i.e. that any unpackaged
install lost the session token on every restart. **Measurement says otherwise**: the freshly
installed unpackaged build showed `Signed in as demo@example.com`, reading a token an earlier
unpackaged run had stored, and no DPAPI file was ever created. The Locker works for unpackaged
installs; a defensive null-guard is not proof of a null.

`MeetVault.Core\DpapiSecretStore.cs` therefore stays only as a **fallback for machines where the
Locker is genuinely unavailable** (no identity AND the OS refusing the vault):
`SecretStores.Choose` prefers the Locker and falls back on null/throw, storing each secret as a
DPAPI-protected file under `%APPDATA%\MeetVault\secrets` (current user only, never plaintext). Tests:
`tests\MeetVault.Core.Tests\SecretStoreTests.cs`. It is a safety net, not a required change — the
installer needs no code change for sign-in persistence.

## Facts about the artifact

- Payload: `MeetVault.exe` + the .NET/WindowsAppSDK payload + `libvosk.dll`/`onnxruntime.dll` +
  `vcruntime140*.dll` + the shipped models (`large-en`, `large-ja`, `langid`) — the same tree the
  portable zip carries, so the installer works offline on first launch.
- `Compression=lzma2/normal` + solid: the ~3.7 GB of Vosk model data barely compresses, so
  max/solid buys a few percent for many extra minutes. Bump it if size ever beats build time.
- Version lives in two places that must move together: `#define AppVersion` in
  `installer\MeetVault.iss` and `<Identity Version>` in `src\MeetVault\Package.appxmanifest`.
- The stable `AppId` in the `.iss` is what makes a later installer an **upgrade** rather than a
  second copy — never change it.

## Not verified here

Sign-in persistence itself **is** verified for the installed build (it read back the token an earlier
unpackaged run had stored). What cannot be simulated here is a machine where the Credential Locker is
genuinely unavailable — i.e. the DPAPI fallback path, which is covered by unit tests only.
