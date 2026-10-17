# LidAwake

Keep a Windows laptop working with the lid closed while a job runs, like a MacBook does. When the job
finishes, every power setting goes back to how it was. If the lid is still closed then, the laptop hibernates:
it powers off with everything saved, and opening it resumes exactly where it was.

![LidAwake tray menu, on and off](assets/menu.png)

Built for long [Claude Code](https://claude.com/claude-code) runs. On Windows, closing the lid puts the laptop to
sleep even when an app asks it to stay awake. That includes the Claude desktop app's *Keep computer awake* toggle,
because Windows keep-awake requests only block *idle* sleep, not the lid. LidAwake changes the lid action itself
for as long as the job runs.

- **Lives in the tray.** Click the coffee cup to turn it on (coral) or off (grey). While off it changes nothing
  and uses no CPU. Right-click for the menu, which includes *Start with Windows* (on by default after `install`).
- **No admin and no network.** One small exe (about 150 KB) that uses the .NET Framework built into Windows.
  No service and no scheduled task.

## What it does while it is on

| | While on | After |
|---|---|---|
| Lid close action (plugged in and on battery) | *Do nothing*: the screen turns off and the PC keeps working | your setting again |
| *Put the computer to sleep after…* and the lock-screen sleep timeout | *Never* (on Modern Standby laptops a keep-awake request alone stops working ~5 min after this timer runs out) | your setting again |
| Turning off the display | unchanged, so the screen still turns off | unchanged |
| Lid closed while on battery | Energy Saver turned on | your setting again |
| Battery drops to 10% with the lid closed | hibernates so nothing is lost; the job carries on when you open it | |
| Job ends while the lid is closed | | hibernates (everything saved, laptop off) |

Your original values are saved to `%LOCALAPPDATA%\LidAwake\restore.txt` **before** anything changes. If
LidAwake is ever killed, the next start puts them back first. You can also run `LidAwake restore` at any time.

## Install

Download `LidAwake.exe` from [Releases](https://github.com/Mqr1oo/LidAwake/releases/latest) and **double-click
it**. That's all:

- it installs itself to `%LOCALAPPDATA%\Programs\LidAwake\`, adds **LidAwake** to the Start menu, sits in the
  tray and starts with Windows;
- if Claude Code is installed, it asks once: *Connect LidAwake to Claude Code?* Click **Yes** and it adds the
  hooks below to `%USERPROFILE%\.claude\settings.json`, keeping everything else and saving a backup first. It
  also adds a short note to `%USERPROFILE%\.claude\CLAUDE.md` so Claude knows the tool exists.

Downloading a newer version and double-clicking it updates in place. `LidAwake connect` / `LidAwake disconnect`
add or remove the Claude Code part later.

## Phone hotspot (optional)

Tick **Phone hotspot** in the tray menu. Now, when you turn LidAwake on yourself, it also turns on your phone's
hotspot and connects the laptop to it, so the work stays online once you leave your Wi-Fi with the laptop in
a bag. It opens the Wi-Fi list for a moment, presses **Connect** on your phone, then checks that the laptop is
really online and tells you either way. If the internet drops while LidAwake is on, it tries again every
2 minutes.

**One-time setup first.** Without it the option can't work:

1. Link your Android phone with **Phone Link** (on the phone: **Link to Windows**). The phone then shows up in
   **Settings → Bluetooth & devices → Mobile devices**.
2. In **Phone Link → Settings → Features → Instant hotspot**, click **Set up** and approve it on the phone.
3. Check it: open the Wi-Fi list on the laptop. Your phone should be listed as *"Mobile hotspot, off"* even
   while its hotspot is off. Clicking it should start the hotspot. If that works, LidAwake can do it.

Keep Bluetooth on, on both the laptop and the phone. Instant hotspot works only with phones that support it
(many Android phones; not iPhone), and it uses your mobile data.

## Uninstall

**Settings → Apps → Installed apps → LidAwake → Uninstall**, like any other app (or run `LidAwake uninstall`).
This puts your power settings back and removes the hooks and the note from Claude Code (the rest of
those files is kept). It also deletes the program and its log, the Start menu entry and the
start-with-Windows entry.

## Use it by hand

Click the coffee cup in the tray, or open **LidAwake** from the Start menu. The cup turns coral. Close the lid
and the work keeps going. Click the cup again when you are done.

## Let Claude Code turn it on and off

The installer adds these for you (see above). To add them by hand instead, merge this into
`%USERPROFILE%\.claude\settings.json`; `LidAwake hooks` prints it with your own path. Claude Code also reads
this file for sessions in the Claude desktop app's Code tab.

```json
{
  "hooks": {
    "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "C:\\Users\\YOU\\AppData\\Local\\Programs\\LidAwake\\LidAwake.exe", "args": ["hook"], "timeout": 10 } ] } ],
    "Stop":             [ { "hooks": [ { "type": "command", "command": "C:\\Users\\YOU\\AppData\\Local\\Programs\\LidAwake\\LidAwake.exe", "args": ["hook"], "timeout": 10 } ] } ],
    "StopFailure":      [ { "hooks": [ { "type": "command", "command": "C:\\Users\\YOU\\AppData\\Local\\Programs\\LidAwake\\LidAwake.exe", "args": ["hook"], "timeout": 10 } ] } ],
    "SessionEnd":       [ { "hooks": [ { "type": "command", "command": "C:\\Users\\YOU\\AppData\\Local\\Programs\\LidAwake\\LidAwake.exe", "args": ["hook"], "timeout": 10 } ] } ]
  }
}
```

How it decides:

- **Sending a prompt** turns LidAwake on for that session.
- **Claude finishing its reply** turns it off for that session, unless background work (agents, workflows,
  background shells) is still running. In that case it stays on until that work finishes too.
- **Several sessions** can hold it at once. It stops 60 seconds after the last one lets go.
- **If Claude Code exits or crashes,** its hold is dropped. A hold that hears nothing for 4 hours is dropped too.
  This covers turns you interrupt with Esc, which send no Stop event.
- **The hook never blocks Claude:** it prints nothing and always exits 0.

## Command line

```
LidAwake              double-click: installs, or turns on once installed
LidAwake start [id]   keep awake for a job          LidAwake stop [id]   that job is done
LidAwake off          stop now                      LidAwake status      show state
LidAwake restore      put power settings back       LidAwake hook        used by the hooks above
LidAwake install      install (= double-click)      LidAwake uninstall
LidAwake connect      add the Claude Code hooks     LidAwake disconnect  remove them
LidAwake on           turn on                       LidAwake quit        leave the tray
LidAwake hooks        print the hooks JSON
```

The log is at `%LOCALAPPDATA%\LidAwake\lidawake.log`.

## Build

Windows 10 or 11 already has everything needed:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces `bin\LidAwake.exe` using the in-box `csc.exe` (.NET Framework 4.x). Set `LIDAWAKE_DRYRUN=1` to
try it without writing any power setting.

## Limits

- Desktop PCs have no lid. On those it only blocks idle sleep.
- If your organisation locks power settings, LidAwake shows a warning in the tray menu.
- A laptop working with the lid shut in a bag gets warm. Energy Saver helps, but don't seal it in a sleeve for hours.

---

### Pe scurt (română)

LidAwake ține laptopul pornit cu capacul închis cât timp lucrează Claude (sau cât îl pornești tu). Ecranul se
stinge, iar pe baterie intră în Energy Saver. Când treaba e gata, inclusiv taskurile din fundal, pune setările
înapoi exact cum erau. Dacă capacul e încă închis, laptopul intră în hibernate: se oprește cu tot salvat. Tot
așa face dacă bateria ajunge la 10% cu capacul închis. Nu rulează în fundal, nu cere
admin și nu folosește internetul. Pornești din Start → **LidAwake** și oprești din iconița cu cana de cafea din tray.

MIT License.
