# Locked-down setup — making blocks genuinely unbypassable

Wolfstare can't be made admin-proof (see the threat model in the design spec §2 — and note that
Cold Turkey isn't admin-proof either). But it **can** be made unbypassable by anyone who is *not*
a local administrator. The trick isn't a driver — it's not handing the blocked person admin
rights.

With this setup, the person being blocked has no way through: they can't stop the service, can't
touch the registry blocks, can't edit or delete the database, and can't uninstall. All of those
require administrator rights they don't have.

## The idea

- Wolfstare runs as the **LocalSystem** service (installed once, by an admin).
- Your everyday Windows account is a **standard (non-admin) user**.
- A **separate administrator account** exists — used only to install, and to remove a block in a
  real emergency. If you want a block you genuinely cannot escape, you must not know (or must not
  be willing to use) that admin password during the block.

What holds against the standard user, and why:

| Escape | Why it fails for a standard user |
|---|---|
| Stop / delete the service | Changing a service needs admin; standard users can't. |
| Delete the IFEO registry keys | They live in `HKLM`, which standard users can't write. They're also ACL'd to require taking ownership. |
| Edit / delete the block database | `%ProgramData%\Wolfstare` is ACL'd to SYSTEM + Administrators only. |
| Uninstall Wolfstare | The uninstaller needs admin, and refuses while a block is locked anyway. |
| Kill the process | Standard users can't stop the service; if they could, it auto-restarts. |

The honest residual: whoever holds the **admin** account can still get through (safe mode, take
ownership, `sc delete`). That's intended — that account is your emergency exit. Keep it, or the
block really is permanent.

## Step 1 — create a separate admin account and make your daily account standard

**This step is yours to do, not the tool's.** Getting it wrong can lock you out of admin, so do
it deliberately. From an **elevated PowerShell** (run as administrator):

```powershell
# Create a dedicated admin account (choose a strong password when prompted).
net user WolfstareAdmin * /add
net localgroup Administrators WolfstareAdmin /add

# Verify you can log in as WolfstareAdmin BEFORE demoting your own account.
# Then, still elevated, remove your daily account from Administrators:
net localgroup Administrators "<your-daily-username>" /delete
```

Do not skip verifying the new admin account works first. If your daily account is your only admin
and you demote it without a working alternative, you lose admin access.

## Step 2 — install Wolfstare as the hardened service

From an **elevated PowerShell** (as an administrator):

```powershell
cd <repo>
scripts\install.ps1
```

This builds the UI, publishes the service, turns on `ModifySystem`, registers the service with
auto-start and always-restart failure actions, and starts it. On first run as a service it locks
the data directory to SYSTEM + Administrators.

## Step 3 — use it as the standard user

Log in as your standard account. Open <http://127.0.0.1:8437/>, create a block list, and start a
random-text block. From here, as the standard user, there is no way to end it early except
retyping the string — every other route needs the admin account.

## Verifying it holds (as the standard user)

- `sc stop Wolfstare` → access denied.
- Deleting a key under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\` → access denied.
- Opening `%ProgramData%\Wolfstare` → access denied.
- `scripts\uninstall.ps1` → refused (needs admin; refuses during a lock regardless).

## Removing it (as the admin account)

Log in as `WolfstareAdmin`, ensure no block is locked (or accept that you're using your emergency
exit), and run `scripts\uninstall.ps1` from an elevated prompt.
