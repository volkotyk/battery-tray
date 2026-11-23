# BatteryTray

A small Windows tray app that shows the battery level of a SteelSeries Arctis 7P headset
and a Razer Basilisk Ultimate mouse. Each device gets its own 16 px icon.

No vendor software is needed: the app talks to the devices over HID directly.

## What the icons show

| Icon | Meaning |
|---|---|
| `73` + green bar | level in percent; amber at 30 % or less, red at 15 % or less |
| animated cyan bar | charging |
| `Zz` | the mouse does not answer for 2 minutes (asleep or switched off) |
| power symbol | the headset is off |
| `--` | receiver or dongle not found |

While a device is silent, the bar keeps the last known level in grey.
The tooltip shows the device name, the level and the connection.

Windows notifications appear once at 20 % and once at 10 %.
They come again after the level goes above 25 % or the device charges.

## Supported devices

Tested only on this hardware:

| Device | USB ID | Request | Reply |
|---|---|---|---|
| SteelSeries Arctis 7P | `1038:12D5`, interface 3, usage `FF43:0202` | output report `06 12` | `06 12 <status> <percent> <power> 00 00 <mV lo> <mV hi>`; status `03` on, `01` off; power `03` battery, `01` cable |
| Razer Basilisk Ultimate | `1532:0086` (cable/dock), `1532:0088` (receiver), interface 0 | 90-byte feature report, transaction `1F`, class `07`, id `80` (level 0-255) and `84` (charging) | Razer status `02` = OK, `04` = mouse does not answer |

The Arctis request comes from [HeadsetControl](https://github.com/Sapd/HeadsetControl).
The Razer report layout comes from [openrazer](https://github.com/openrazer/openrazer).
The Arctis power byte was found on real hardware and is not in HeadsetControl.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish src/BatteryTray.csproj -c Release -o publish
```

The result is one framework-dependent `publish\BatteryTray.exe` (about 190 KB).
It needs the .NET 8 Desktop Runtime.

## Use

- Start `BatteryTray.exe`. Right-click an icon for **Refresh**, **Start with Windows** and **Exit**.
- `--log` writes the raw device replies to `%LOCALAPPDATA%\BatteryTray\raw.log`.
- `probe/` is a console tool that reads both devices with the same code:
  `dotnet run --project probe -- watch 5` prints the state every 10 s for 5 minutes.

## Notes

- If the process starts with admin rights (for example with UAC off), it starts itself again
  with a normal-user token. HID access does not need admin rights.
- On the first start the app sets its two tray icons to "show on the taskbar".
  A later change in Settings > Personalization > Taskbar stays as you set it.
- Notifications use WinRT toasts with the AppUserModelID `BatteryTray`,
  registered under `HKCU\Software\Classes\AppUserModelId`.
- Other software that talks to the same Razer device (for example OpenRGB) can run at the same time.
  The app accepts only the reply to its own command.

## License

[MIT](LICENSE)
