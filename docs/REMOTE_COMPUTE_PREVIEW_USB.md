# Remote compute on preview, phone over USB

Prove that an Android phone with Wi-Fi off can use the Windows local-service through the preview relay. The phone is on mobile data. USB is only for install and `adb logcat`. It is not the compute path.

Both sides must call the same API. The invite is created on that API and is useless against any other host. Production (`Api:HeadlessBaseUrl`) does not serve `/compute/pairs`. Preview (`Api:HeadlessBaseUrl-Preview`) does. Use `https://headless-m3u8-preview.example.test` as the stand-in; `package-android.ps1` prints the baked address.

## What you need

- The signed-in Auth0 user has the `relay-user` role or permission. Sign out and back in after the role is added. The access token in the app does not pick up a new role until then.
- Without `relay-user`, the share and "use another user's local-service" switches stay hidden.
- Phone USB debugging on, Wi-Fi off, and it is the only `adb` device. `adb` here is `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`.
- User-secrets already contain `Api:HeadlessBaseUrl-Preview`. `-Api preview` copies that value into `Api:HeadlessBaseUrl` for one package. It does not change the stored secret.
- The installed Windows service `VardyPartyLocalService` is stopped. The from-source process owns port 5019.

## Menu

After the build that includes the exclusive switches, each device is in one of three states:

- **Neither.** Both switches off. This is the start.
- **Share my local-service.** Windows only. Android does not show this switch. Turning it on hides the other switch and shows an invite code. Turning it off returns to neither.
- **Use another user's local-service.** The phone. Turning it on hides share and asks for the invite code. Turning it off returns to neither.

Do not share and redeem on the same device.

## 1. Local-service

From `M3U8-resolver`:

```powershell
pwsh ./scripts/run-from-source.ps1
```

Leave that window open. It listens on `http://0.0.0.0:5019` and announces on UDP 40129. The phone will not see that UDP advertisement while Wi-Fi is off. That timeout is expected. The Windows app reaches the service on the PC.

## 2. Windows client on preview

A debug Windows build reads `VARDYPARTY_DEBUG_API` at startup (`local`, `preview`, or `production`). Unset means production. A shell variable is not enough for the packaged app. Set it for the user, then start the app in a new process:

```powershell
[Environment]::SetEnvironmentVariable("VARDYPARTY_DEBUG_API", "preview", "User")
pwsh ./scripts/run-windows-debug.ps1
```

Run that from `VardyParty-Client`. Close any `VardyParty` process first if the script is only relaunching an already-registered package, so the new process sees the variable.

Confirm the client log, not the Application event log. HTTP lines go to:

`%LOCALAPPDATA%\VardyParty\logs\vardyparty-yyyyMMdd.log`

A share attempt must say `POST https://headless-m3u8-preview.example.test/compute/pairs` (or whatever `package-android.ps1` / the Windows debug log printed for preview). A `404` on the production stand-in `https://headless-m3u8.example.test` means this process is still on production.

Sign in, open the menu, turn on **Share my local-service**. The log should then show `200` on `/compute/pairs` and `200` on `POST http://…:5019/compute/host/start`. The menu shows an 8-character invite code. The local-service console logs `Compute relay {correlationId} host connected`.

## 3. Phone APK on preview

Release builds ignore `VARDYPARTY_DEBUG_API`. Bake preview in:

```powershell
pwsh ./package-android.ps1 -Api preview
```

ILLink wants a lot of RAM. The build log must contain:

`API target preview: Api:HeadlessBaseUrl = https://headless-m3u8-preview.example.test/`

`package-android.ps1` can leave secrets in `VardyParty/appsettings.json`. Restore that file before any commit:

```powershell
git restore VardyParty/appsettings.json
```

Install, with Wi-Fi still off:

```powershell
adb install -r VardyParty/bin/Release/net11.0-android/com.vardyparty-Signed.apk
```

Sign out on the phone and sign back in so the token includes `relay-user`. Turn on **Use another user's local-service**, enter the code from the PC, and confirm. The phone log must show:

`POST https://headless-m3u8-preview.example.test/compute/pairs/redeem`

and `200`. The status is `Using another user's local-service.` The code is single-use. A `404` from redeem does not consume it, but it does mean the phone is on the wrong host. `Could not redeem that invite code` on a non-404 means the code was rejected for another reason. A `404` specifically is `This API does not offer remote compute` on a build that includes that message.

Tail the phone once it is running:

```powershell
adb logcat --pid=$(adb shell pidof -s com.vardyparty) -v time
```

## 4. Play a match

Do this when the catalog has games. Preview can return `404` for the games catalog (`[Api] Games catalog is empty (404)` and `Games updated count=0`). That is an empty board, not a failed pair.

On the phone, pick a match. LAN discovery will time out on UDP ports `40129`, `40143`, `40157`, `40171`, and `40189`. That is the Wi-Fi-off path. The resolve should then go through the paired host. Watch:

- Phone logcat for `[RemoteCompute]` and any `This phone failed` line. A correlation id is in the user-facing fault.
- The Windows client file log above.
- The `run-from-source.ps1` console for `Compute relay` lines.

## Failures seen so far

| What you see | Cause |
| --- | --- |
| `POST …example.test/compute/pairs` `404` | That app is on production. Windows: `VARDYPARTY_DEBUG_API=preview` and relaunch. Phone: `package-android.ps1 -Api preview` and reinstall. |
| `This account needs the relay-user role` | Token has no `relay-user`. Add the role, then sign out and in. |
| Redeem `404` while the PC created the code on preview | Phone and PC are on different hosts. The code was not consumed. |
| Phone UDP discovery timeouts | Expected with Wi-Fi off. |
| Preview catalog `404`, 0 games | Preview has no board right now. Pairing can still succeed. |
