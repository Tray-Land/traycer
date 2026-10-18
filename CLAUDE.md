@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app built from the `winui-tray-app` skill scaffold on top of `winapp new`. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`Views/TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden. Anything it starts, it must stop in `OnHidden` / `Dispose` of the page that started it.
- **Closing must actually free the window.** `CloseWindow` unsubscribes every `DispatcherQueueTimer.Tick` and calls `WindowManager.Get(this).Dispose()`: WinUIEx 2.9.3 never disposes the manager a `WindowEx` creates, and its AppWindow and timer subscriptions keep every closed flyout alive (a plain `Window` is collected; a `WindowEx` isn't). To check, hold a `WeakReference` to the closed window and confirm it dies after `MemoryService.ReleaseIdle`; `dotnet-dump`'s `gcroot` can't see these native holds.
- Put pure decision logic (parsing, policies, formatting) in classes with no WinRT dependency, so a plain `net10.0` test project can cover it.
- The repo is MIT licensed (`LICENSE`). Keep `README.md` in step with the features.

## Traycer specifics

- **Background polling is the point.** The tray icon shows print progress, so `Services/PrinterHub` runs one `PrinterMonitor` per printer for the app's whole life (the documented exception to "no work while hidden"). Cadence lives in `TrayCer.Core/Monitoring/PollPolicy.cs`: active interval while printing or while the flyout is open, a minute when idle, three minutes for idle push printers (their connection is dropped), backoff to five minutes when unreachable. Don't add work outside it.
- **Protocols live in `TrayCer.Core/Drivers`**, one folder per protocol: a `*Driver` (transport) and a `*Parser` (JSON or text to `PrinterSnapshot`, unit tested against `TrayCer.Tests/Samples`). Every snapshot field except `State` is optional; the cards and the detail page show only what's set. A new protocol needs a `PrinterKind` value (append, never renumber), a `PrinterKindInfo` entry (what the add form asks for), a factory case, and a `PrinterProbe` check if it can be detected.
- **Secrets** (API keys, access codes, passwords, check codes) go through `Services/AccessCodeVault` (PasswordVault, keyed by printer id). `SettingsService.Printers` holds only non-secret config. `Services/CredentialStore.cs`, `Services/Secrets.cs` and `OAuth.resw` are unused scaffold leftovers; the app has no keys of its own.
- **Tray icon**: `TrayCer.Core/Presentation/TrayIndicator` decides what to show for the fleet, `TrayCer.Core/Icons/TrayIconPainter` draws it, `Services/TrayIconRenderer` owns the HICON. The renderer also owns the finished marquee's frame timer, which runs only while `TrayGlyph.Done` is showing.
- **Finished prints wait to be marked complete.** `PrinterStatus.NeedsMarkComplete` is separate from `Attention`: opening the flyout acknowledges warnings and errors but not a finish. Only the card's or detail page's **Mark as complete** (`PrinterHub.MarkComplete`) or the next print starting (`TransitionPolicy.NeedsMarkCompleteAfter`) clears it. `PrinterHub` saves the set in `SettingsService.NeedsMarkComplete`, so it survives restarts and printer edits.
- **No printer handy?** `dotnet run --file scripts/FakePrinters.cs` starts fake Bambu, Klipper, OctoPrint, Creality, Elegoo and FlashForge printers on 127.0.0.1 (addresses and codes in the file's header). Use `--file`: plain `dotnet run` from the root runs the app instead.
- Tests: `dotnet test TrayCer.Tests` (Core only; verify XAML with `winapp ui` against the running app, with the fake printers).
