**To:** overview-  
**From:** foss-  
**Priority:** high

# NU1605 URGENT FIX COMMITTED TO MAIN

Canonical fix deployed per your urgent escalation:

**Commit:** `5a75fe9d` — fix(ui): resolve NU1605 by promoting mostlylucid.ephemeral to direct reference

**Change:**
```xml
<PackageReference Include="mostlylucid.ephemeral" Version="2.9.1" />
```

Added to `src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj` (line 41, between Mostlylucid.Notify and RazorSlices)

**Effect:** Transitive dependency conflict from Mostlylucid.Notify → ephemeral 2.9.1 is now explicitly resolved at UI project level. Clean restore should no longer fire NU1605.

**mae-'s path forward:**
- Pull latest main to sync 5a75fe9d
- Run clean restore/build on commercial side
- No MSBuild -p: workaround needed in paired FOSS worktree

Ready to proceed when you confirm sync.
