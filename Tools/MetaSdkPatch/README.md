# Meta XR Core SDK patch for Unity 6000.5

**Symptom:** opening the project offers Safe Mode, with

```
Library\PackageCache\com.meta.xr.sdk.core@<hash>\Editor\BuildingBlocks\BlockData\MultiplayerBlocks\NGO\SceneListenerNGO.cs(57,56):
error CS0619: 'CreateGameObjectHierarchyEventArgs.instanceId' is obsolete: 'instanceId is deprecated. Use entityId instead.'
```

**Cause:** an upstream bug in Meta XR Core SDK 205.0.0, not in this project's code.
Unity 6000.5 made `ObjectChangeEventStream`'s `instanceId` an *error*-level obsolete.
`SceneListenerNGO.cs` guards it as:

```csharp
#if UNITY_6000_3_OR_NEWER
    EditorUtility.EntityIdToObject(evt.instanceId)   // still reads .instanceId
#else
    EditorUtility.InstanceIDToObject(evt.instanceId)
#endif
```

Meta swapped the conversion *function* for 6000.3+ but not the *property*, so both
branches fail on 6000.5. They already fixed the identical code in the sibling file
`Editor/OVRTelemetry/OVRSceneChangeListener.cs`, which has the correct three-way guard
with a `UNITY_6000_5_OR_NEWER` branch using `.entityId`. This patch adds that same
branch to `SceneListenerNGO.cs`.

**Fix:**

```powershell
powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1
```

Then return to Unity and let it recompile.

## Why this is a script and not a committed file

The patch has to land in `Library/PackageCache`, which is gitignored and is rebuilt from
the registry tarball whenever the package re-resolves — a fresh clone, a cleared cache, a
version bump, or deleting `Library/`. The alternative (embedding the package under
`Packages/` so UPM stops managing it) would vendor ~400 MB into the repo for a six-line
change. So: keep the six lines here, reapply on demand.

**Re-run this script after any fresh clone or package re-resolve.** It is idempotent —
running it when the patch is already applied does nothing.

## When to delete this folder

When Meta ships a Core SDK release that compiles on 6000.5. Bump
`com.meta.xr.sdk.core` in `Packages/manifest.json`, run the script, and if it reports
that it could not match the broken code, the upstream fix has landed — delete
`Tools/MetaSdkPatch` and this README with it.
