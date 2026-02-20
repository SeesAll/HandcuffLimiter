# HandcuffLimiter

**Author:** SeesAll\
**License:** GNU General Public License v3.0

HandcuffLimiter is a Rust plugin designed to prevent restraint
abuse by enforcing a maximum restraint duration.\
If a player remains restrained too long, the plugin can warn, remove
restraints, and safely teleport the victim to Outpost or Bandit Camp 
and provide the captive an opportunity to punish or forgive their captor(s).

------------------------------------------------------------------------

# Features

-   Maximum restraint duration enforcement
-   Optional warning before limit is reached
-   Optional Punish / Forgive decision flow
-   Reliable removal of handcuffs and hood
-   Safe teleport validation (prevents teleporting inside buildings)
-   Outpost or Bandit Camp destination selection
-   Teleport caching and blacklist protection
-   Admin monitoring commands
-   Permission-based exemptions

------------------------------------------------------------------------

# Installation

1.  Place `HandcuffLimiter.cs` into:

        oxide/plugins/

2.  Reload plugin:

        oxide.reload HandcuffLimiter

3.  Edit config located at:

        oxide/config/HandcuffLimiter.json

------------------------------------------------------------------------

# Configuration Guide

Below is a full explanation of configuration options.

## Restraint Settings

### `MaxRestrainMinutes`

Maximum time (in minutes) a player can remain restrained before
enforcement triggers.

### `WarnSecondsBeforeLimit`

How many seconds before the limit the warning message should be shown.

### `EnablePunishForgivePrompt`

If enabled, presents a Punish/Forgive decision instead of automatically
enforcing.

------------------------------------------------------------------------

## Teleport Settings

### `Teleport Destination (outpost / bandit)`

Selects where players are teleported after enforcement.

Valid values: - `"outpost"` - `"bandit"`

### `TeleportAttempts`

Number of candidate teleport positions attempted before giving up.

### `TeleportCandidateRadius`

Radius around the monument used to sample potential landing spots.

### `BuildingProximityRejectRadius`

Minimum allowed distance from buildings and structures.\
Higher values = safer but fewer valid spots.

### `TeleportClearanceRadius`

Radius used for collision checks at the landing position.

### `TeleportClearanceHeight`

Vertical clearance used when validating landing space.

### `DestinationOffset`

Final positional adjustment applied to the teleport location.

Example:

``` json
"DestinationOffset": {
  "x": 0.0,
  "y": 1.5,
  "z": 0.0
}
```

-   `y` is typically left at `1.5` to prevent terrain clipping.

------------------------------------------------------------------------

## Safety & Cooldown Settings

### `TeleportCooldownSeconds`

Minimum time between teleport attempts for a player.

### `EnableBlacklistCache`

Stores failed teleport spots temporarily to prevent retrying bad
positions.

------------------------------------------------------------------------

## Permissions

-   `handcufflimiter.admin`\
    Grants access to admin commands.

-   `handcufflimiter.exempt`\
    Exempts player from restraint enforcement.

------------------------------------------------------------------------

# Commands

### Chat Commands

-   `/hcl status <player>`\
    View restraint status.

-   `/hcl clearcache outpost|bandit|all`\
    Clears teleport cache.

### Console Commands

-   `hcl.clearcache outpost|bandit|all`

------------------------------------------------------------------------

# License

This project is licensed under the GNU General Public License v3.0.\
See the LICENSE file for full legal terms.
