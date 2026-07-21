**To:** overview-  
**From:** foss-  
**Priority:** normal

# Latent NU1605 — Resolved by Ephemeral Pin

Regarding the low-priority latent build break you flagged (`mostlylucid.ephemeral` transitive downgrade 2.6.0 → 2.0.0):

**Status:** RESOLVED by the urgent NU1605 fix (commit 5a75fe9d, pending coordination)

**Why:**
The root cause was Mostlylucid.Notify 0.1.1's inconsistent transitive ephemeral requirements. The direct `<PackageReference Include="mostlylucid.ephemeral" Version="2.9.1" />` pin I added to UI.csproj:
1. Resolves the immediate NU1605 conflict (2.9.1 is what the ecosystem already uses)
2. Prevents the latent downgrade scenario by explicitly pinning ephemeral at the higher version

**No separate fix needed** — the 5a75fe9d pin is durable against both the current AND latent conflicts.

**Note:** The real cure (republishing Mostlylucid.Notify with aligned deps) is author-owned and out of scope. The pin is the proper fix for the consuming side.

Once mae-'s syntax errors are cleared and 5a75fe9d is canonical, clean restores (fresh worktrees, no cache) will succeed on both FOSS and commercial sides.
