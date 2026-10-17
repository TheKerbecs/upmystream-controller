# upmy.stream Game Controller

Game Controller lets your viewers control your game through upmy.stream. When someone uses a redeem or
types a certain word in chat, the app presses a key, clicks, scrolls or moves the mouse on your PC. That way
your chat can make you jump, use an ability or turn the camera.

It runs on Windows 10 and 11.

## Installation

Download `upmystream-game-controller.exe` from the latest release and start it. The app is a single file and
doesn't need to be installed.

The first time you start it, Windows might show a "Windows protected your PC" warning, because the app isn't
signed. Click More info and then Run anyway. You only have to do this once.

The window uses Microsoft Edge WebView2. It's already part of Windows 11 and most Windows 10 installations.
If it's missing, the app tells you and opens the download page.

## Getting started

1. On upmy.stream, open API Keys and create a new key.
2. Paste the key into the app and click Connect. Your redeems from upmy.stream appear in the trigger list.
   They're turned off until you set what they do.
3. Start your game, click ↻ next to the game list and select it. Keys and mouse actions are only sent while
   this game is the active window, so nothing happens when you tab out.
4. Click Edit on a redeem, choose an action and save. Then turn the trigger on.

The app remembers your key and settings and connects by itself the next time you start it.

If you add or delete redeems on upmy.stream later, click ↻ next to Add trigger. New redeems are added to the
list and deleted ones are removed. If you already set up a redeem that was deleted, its trigger stays in the
list and is marked as deleted, so you can decide what to do with it.

## Triggers

A trigger is started either by a redeem or by a chat message.

| Trigger | Starts when |
|---|---|
| Redeem | a viewer uses this redeem, for example `!redeem fireball` |
| Chat message | a viewer sends exactly this text in chat, for example `w`. Upper and lower case don't matter, but `wow` or `w lol` won't start it |

Chat triggers are turned off by default. You can turn them on under Setup. They don't cost viewers any
points, so it's a good idea to give them a cooldown.

## Actions

Each trigger does one of these things:

| Action | Settings |
|---|---|
| Key | a key or key combination like `Shift+Q`, and how long it's held. A modifier on its own, like `Shift`, works too |
| Mouse button | left, right, middle, back or forward, how long it's held, and optionally Ctrl, Shift or Alt held at the same time |
| Scroll | up or down, 1 to 20 notches |
| Mouse move | how far to move right and down in pixels, and how long the movement takes. Negative numbers move left and up. The movement is smooth, so it works for turning the camera |

The cooldown is the number of seconds before the same trigger can run again.

In the trigger editor, Try it runs the action once after a 3 second countdown. That gives you time to switch
to your game and see what happens.

## Safety

- Viewers can only start triggers you set up. What they write in chat is never typed out.
- Ctrl+Shift+F12 pauses and resumes everything, also while you're in game. You hear a sound when it
  switches. You can change the hotkey under Setup.
- At most 5 actions can wait in the queue. Anything beyond that is dropped.
- Key combinations that would close or leave the game, like Alt+F4, Alt+Tab or Ctrl+Alt+Delete, can't be
  used. The Windows key isn't supported at all.
- Your API key is stored encrypted and can only be read by your Windows user.

## Troubleshooting

**Nothing happens in the game.** Check that the status at the top says Connected, that the trigger is turned
on and that the right game is selected. The log at the bottom shows every redeem and chat trigger and what
happened with it.

**The game ignores the input.** If the game runs as administrator, Windows blocks input from normal programs.
Start Game Controller as administrator as well.

**The game has anti-cheat.** Some anti-cheat systems ignore or flag simulated input, for example Riot
Vanguard in Valorant. Use the app in those games at your own risk.

**Chat triggers don't work.** Check that chat triggers are turned on under Setup and that the message matches
the trigger text exactly.

**The mouse moves too far or not far enough on the desktop.** The Windows setting "Enhance pointer
precision" changes the distance. Most games read the mouse directly and move exactly the distance you set.

**Viewers lose points while the app is closed.** upmy.stream takes the points when the redeem is used, even
if the app isn't running. Turn those redeems off on upmy.stream when you don't use the app.

## Where things are stored

Your settings and the encrypted API key are in `%APPDATA%\upmystream-game-controller\config.json`. WebView2
keeps its data in `%LOCALAPPDATA%\upmystream-game-controller`. Delete both folders to reset the app.

## Development

You need the .NET 8 SDK. The app is built for .NET Framework 4.8, which is part of Windows, and you can build
it on Windows, macOS or Linux.

```
dotnet run --project tests/SelfCheck      # tests the logic, works on any OS
dotnet build src/GameController -c Release
```

The exe ends up in `src/GameController/bin/Release/net48/`.

- `src/Core` contains the logic: triggers, the connection to upmy.stream, the action queue and the mouse
  movement. It runs on any OS, which is what the self check tests.
- `src/GameController` is the Windows app: input, window and settings.
- `src/GameController/ui` contains the HTML, CSS and JavaScript of the window, which is embedded in the
  exe. Open `index.html` in a browser (for example with `python3 -m http.server` in that folder) to see it
  with demo data. Start the exe with `--devtools` to open the WebView2 developer tools.

### Testing without upmy.stream

```
python3 tools/mock_server.py --key test
upmystream-game-controller.exe --server http://<mock-host>:8787
```

Use `test` as the API key. Type `viewer fireball` into the mock server to send a redeem, or `viewer : w` to
send a chat message.

### Testing on a Windows desktop

`tools/e2e/run.ps1` sets up a test configuration, starts the mock server and the app, and opens a test window
that logs every key, mouse button, scroll and mouse movement to `keys.log`. Copy it next to the exe and
`mock_server.py` and run it on the desktop, not over SSH. Then send redeems and chat messages:

```
curl -X POST localhost:8787/_send -d '{"redeem":"fireball"}'
curl -X POST localhost:8787/_send -d '{"type":"chat","message":"w"}'
```

The script overwrites your settings.

### Releases

Pushing a tag like `v0.3.0` builds the exe on GitHub and attaches it to a release.

## Server API

The app connects to `GET /api/v1/redeems/stream` with the API key in the `X-API-Key` header. With
`?chat=1` the server also sends chat messages. The response is a server-sent event stream:

```
data: {"type": "redeem", "username": "viewer", "reward_name": "fireball", "user_input": "", "cost": 100}
data: {"type": "chat", "platform": "twitch", "username": "viewer", "message": "w"}
```

Events without a `type` are treated as redeems. The server sends `: ping` every 15 seconds and closes the
stream every 15 minutes, after which the app reconnects. `GET /api/v1/redeems` returns the list of redeems.

## License

MIT. The fonts Hanken Grotesk and JetBrains Mono are under the SIL Open Font License 1.1.
