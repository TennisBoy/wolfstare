# Manual E2E checklist — Phase 3 website enforcement

The DNS/proxy servers and the mutator logic are covered by automated tests. The Windows
adapters that change machine configuration are not — a mocked test of `netsh` or a registry
write proves only that the mock was called (spec §13). This checklist is how that half is
verified, on real hardware, by hand.

**Run an elevated shell.** All of these need administrator rights. Nothing here should be run
on a machine you cannot afford to have its DNS briefly disrupted.

## 0. Baseline

- [ ] Note current DNS: `netsh interface ipv4 show dnsservers name="Wi-Fi"` (use your adapter's name).
- [ ] Note current proxy: `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings" /v ProxyEnable`.
- [ ] `nslookup reddit.com` resolves to a real address.

## 1. Sinkhole, non-privileged (ModifySystem=false)

- [ ] Start: `dotnet run --project src/Wolfstare.Service -- --Wolfstare:DataDirectory=C:/temp/wolf --Wolfstare:Enforcement:ModifySystem=false`.
- [ ] Create a block list containing `reddit.com` and start a timed session (see README for the curl/PowerShell recipe).
- [ ] `nslookup reddit.com 127.0.0.1` (add `-port=<dnsPort>` if not 53) returns `127.0.0.1`.
- [ ] `nslookup example.com 127.0.0.1` returns example.com's real address (forwarded).
- [ ] Confirm the machine's own DNS is unchanged: `netsh interface ipv4 show dnsservers name="Wi-Fi"` matches the baseline.

## 2. Full takeover (ModifySystem=true)

- [ ] Restart with `--Wolfstare:Enforcement:ModifySystem=true` and a domain session active.
- [ ] `netsh interface ipv4 show dnsservers name="Wi-Fi"` now shows `127.0.0.1`.
- [ ] `reg query "HKLM\SOFTWARE\Policies\Google\Chrome" /v DnsOverHttpsMode` shows `off`.
- [ ] `netsh advfirewall firewall show rule name="Wolfstare-DNS-Egress-UDP"` shows the rule present.
- [ ] In a browser, a blocked site shows the block page (HTTP) or a connection error (HTTPS); an allowed site loads.
- [ ] `ProxyServer` under Internet Settings points at the proxy port; an already-open browser picks it up.

## 3. Restore

- [ ] Stop the session (unlocked) or let a short timed lock expire.
- [ ] DNS returns to the baseline value from step 0.
- [ ] The Chrome DoH policy value is gone (was absent at baseline) or back to its prior value.
- [ ] The firewall rule is gone.
- [ ] `nslookup reddit.com` resolves normally again.

## 4. Crash recovery

- [ ] With a domain session active and `ModifySystem=true`, kill the service with `taskkill /F /IM Wolfstare.Service.exe` **without** stopping the session.
- [ ] Confirm DNS is still `127.0.0.1` (enforcement outlived the process — the point of it).
- [ ] Restart the service.
- [ ] The `mutation_journal` table drove a restore-then-reapply: DNS is `127.0.0.1` again and the session is still counting down.
- [ ] Now let the session end and confirm DNS returns to baseline — the journal still held the true original across the crash.

## 5. Adapter removed mid-session (unresolved-entry path)

- [ ] With a session active over Wi-Fi and `ModifySystem=true`, disable the Wi-Fi adapter.
- [ ] Restart the service; confirm it logs that the journal key could not be resolved and keeps serving.
- [ ] Re-enable the adapter, restart again; confirm DNS restores.
