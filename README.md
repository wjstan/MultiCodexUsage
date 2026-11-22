# MultiCodexUsage for Windows

This fork is based on Michael Guber's [Codex Usage for Windows](https://github.com/guberm/Codex-Usage-Windows). It adds multiple ChatGPT accounts and shows each account's limits and reset times in one panel. The original copyright and terms are preserved in [LICENSE](LICENSE).

A tiny, native Windows companion for Codex usage. Keep a **190 × 36 DIP floating pill** near your taskbar: remaining percentage and time until reset. Click for details; drag to move.

![Compact preview with example data](docs/compact.png)

## Build and run

1. Follow the [build instructions](#build-and-verify) to generate `artifacts/release/MultiCodexUsage.exe`.
2. Keep it in a permanent folder and run it. Windows 10/11 x64; no separate .NET installation or administrator rights required.
3. Click **Sign in with ChatGPT**, enter the displayed code in the browser, and authorize your account. If requested, enable device code authorization under ChatGPT **Settings → Security**.
4. Click the header to collapse. Drag the pill where you want it. Right-click for refresh, positioning, hide and exit.

The executable is unsigned. Release checksums are provided in `SHA256SUMS.txt`.

## Daily use

- **Accounts:** use **Add account** and authorize the desired ChatGPT account in the browser. The details panel shows every account's limits and reset times together. Select a limit card to show it in the tray, or use the account selector to choose which account to manage. Refresh updates all saved accounts. **Sign out selected account** removes that account from the app.
- **Floating pill:** percentage remaining, countdown, always-on-top toggle, remembered position. Choose any limit by clicking its card.
- **Details:** all reported Codex primary/secondary and additional limits (including Spark), local reset dates, banked resets (count and the next expiration date/time) and credit balance.
- **Tray:** large, bold, high-contrast remaining-percentage digits fill the icon; click to show details. Launching the app again restores the existing instance. Collapsing details returns to tray-only mode.
- **Refresh:** manually, every 1/3/5/10/15/30/60 minutes (choose in the panel; default 5), when Windows unlocks, resumes, or network connectivity returns.
- **Notifications:** when the primary Codex remaining percentage changes by at least 1%, and when a limit's reset time passes (so you know your allowance came back); configurable in the panel. Windows notification settings / Do Not Disturb can suppress alerts.
- **Banked resets:** each action requires confirmation. A request ID is saved before sending; an uncertain request can be retried using **Retry reset**, including when the reported credit balance has already fallen to zero.
- **Appearance:** follows the Windows app light/dark preference. Enter/Space toggles the focused header; Escape collapses details.
- **Startup:** opt in with **Start with Windows**. If upgrading from `CodexUsage.exe` or moving the executable, turn this option off and back on in `MultiCodexUsage.exe` to update the startup path.

This is a floating window above the taskbar work area, not an embedded Windows taskbar extension. A `!` marks stale data or a refresh error; an elapsed countdown does not invent a new allowance. If a monitor is removed, the window is brought back onto an available screen. Use the tray's **Move near taskbar** to recover its position.

<details><summary>Expanded preview (example data)</summary>

![Expanded preview with example data](docs/expanded.png)

</details>

## Privacy and authentication

Uses the same device authorization flow and usage/reset API contract as [Codex Usage for Android](https://github.com/guberm/Codex-Usage-Android). No API keys, browser cookies or imported Codex sessions. OAuth credentials are encrypted with **Windows DPAPI, CurrentUser**. Each added account has its own folder under `%LOCALAPPDATA%\CodexUsage\accounts` for its credentials, usage cache, pending reset and refresh journal. Existing single-account data remains readable from `%LOCALAPPDATA%\CodexUsage`, where shared preferences are also stored. The refresh journal (`activity.jsonl` plus one rotated `activity.1.jsonl`, each at most 1 MiB) records UTC time, refresh source, result and duration, HTTP status/error type, and prior/current limit names, percentages and reset times. It does not contain tokens, authorization headers, device codes, account identifiers, reset request IDs or raw response bodies. No telemetry or third-party server is used.

Requests go to `auth.openai.com` for authentication and `chatgpt.com/backend-api/wham/` for usage and resets. This internal API may change; the app is an unofficial companion and is not affiliated with OpenAI. Local DPAPI protection does not protect against malicious software already running as your Windows user.

**Sign out selected account** deletes that account's stored credentials, usage cache, pending reset state and both refresh-journal files. To remove the app: disable **Start with Windows**, choose **Exit**, delete the executable, and optionally remove `%LOCALAPPDATA%\CodexUsage`.

## Build and verify

Windows with the .NET 10 SDK:

```powershell
./build.ps1
```

Produces a self-contained Windows x64 `MultiCodexUsage.exe` and `SHA256SUMS.txt` in `artifacts/release/`. Runs local checks for account isolation, DPAPI, device-code parsing, token refresh, reset idempotency and bounded refresh logging. The WPF smoke test verifies that the panel shows multiple accounts with independent limits and reset times. Images and the result are saved in `artifacts/smoke/`.

```powershell
dotnet run --project tests/Checks.csproj
./artifacts/publish/MultiCodexUsage.exe --demo
```

`--demo` uses clearly labelled example data and temporary storage; no authentication, reset, startup or usage requests are made. Automated checks use synthetic responses. A successful build does not establish live account authorization or successful consumption of a real reset credit.

## License

MIT. Copyright (c) 2026 Michael Guber.
